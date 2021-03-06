﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Services.TimerService;
using EventStore.Core.TransactionLog.Checkpoint;
using Microsoft.Extensions.Logging;

namespace EventStore.Core.Services
{
    public enum ElectionsState
    {
        Idle,
        ElectingLeader,
        Leader,
        NonLeader,
        Shutdown
    }

    public class ElectionsService : IHandle<SystemMessage.BecomeShuttingDown>,
        IHandle<SystemMessage.SystemInit>,
        IHandle<GossipMessage.GossipUpdated>,
        IHandle<ElectionMessage.StartElections>,
        IHandle<ElectionMessage.ElectionsTimedOut>,
        IHandle<ElectionMessage.ViewChange>,
        IHandle<ElectionMessage.ViewChangeProof>,
        IHandle<ElectionMessage.SendViewChangeProof>,
        IHandle<ElectionMessage.Prepare>,
        IHandle<ElectionMessage.PrepareOk>,
        IHandle<ElectionMessage.Proposal>,
        IHandle<ElectionMessage.Accept>
    {
        private static readonly TimeSpan LeaderElectionProgressTimeout = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan SendViewChangeProofInterval = TimeSpan.FromMilliseconds(5000);

        private static readonly ILogger Log = TraceLogger.GetLogger<ElectionsService>();
        private static readonly IPEndPointComparer IPComparer = new IPEndPointComparer();

        private readonly IPublisher _publisher;
        private readonly IEnvelope _publisherEnvelope;
        private readonly VNodeInfo _nodeInfo;
        private readonly int _clusterSize;
        private readonly ICheckpoint _writerCheckpoint;
        private readonly ICheckpoint _chaserCheckpoint;
        private readonly IEpochManager _epochManager;
        private readonly Func<long> _getLastCommitPosition;
        private readonly int _nodePriority;

        private int _lastAttemptedView = -1;
        private int _lastInstalledView = -1;
        private ElectionsState _state = ElectionsState.Idle;

        private readonly HashSet<Guid> _vcReceived = new HashSet<Guid>();
        private readonly Dictionary<Guid, ElectionMessage.PrepareOk> _prepareOkReceived = new Dictionary<Guid, ElectionMessage.PrepareOk>();
        private readonly HashSet<Guid> _acceptsReceived = new HashSet<Guid>();

        private MasterCandidate _masterProposal;
        private Guid? _master;
        private Guid? _lastElectedMaster;

        private MemberInfo[] _servers;

        public ElectionsService(IPublisher publisher,
                                VNodeInfo nodeInfo,
                                int clusterSize,
                                ICheckpoint writerCheckpoint,
                                ICheckpoint chaserCheckpoint,
                                IEpochManager epochManager,
                                Func<long> getLastCommitPosition,
                                int nodePriority)
        {
            if (publisher is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.publisher); }
            if (nodeInfo is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.nodeInfo); }
            if ((uint)(clusterSize - 1) >= Consts.TooBigOrNegative) { ThrowHelper.ThrowArgumentOutOfRangeException_Positive(ExceptionArgument.clusterSize); }
            if (writerCheckpoint is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.writerCheckpoint); }
            if (chaserCheckpoint is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.chaserCheckpoint); }
            if (epochManager is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.epochManager); }
            if (getLastCommitPosition is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.getLastCommitPosition); }

            _publisher = publisher;
            _nodeInfo = nodeInfo;
            _publisherEnvelope = new PublishEnvelope(_publisher);
            _clusterSize = clusterSize;
            _writerCheckpoint = writerCheckpoint;
            _chaserCheckpoint = chaserCheckpoint;
            _epochManager = epochManager;
            _getLastCommitPosition = getLastCommitPosition;
            _nodePriority = nodePriority;

            var ownInfo = GetOwnInfo();
            _servers = new[]
            {
                MemberInfo.ForVNode(nodeInfo.InstanceId,
                                    DateTime.UtcNow,
                                    VNodeState.Initializing,
                                    true,
                                    nodeInfo.InternalTcp, nodeInfo.InternalSecureTcp,
                                    nodeInfo.ExternalTcp, nodeInfo.ExternalSecureTcp,
                                    nodeInfo.InternalHttp, nodeInfo.ExternalHttp,
                                    ownInfo.LastCommitPosition, ownInfo.WriterCheckpoint, ownInfo.ChaserCheckpoint,
                                    ownInfo.EpochPosition, ownInfo.EpochNumber, ownInfo.EpochId, ownInfo.NodePriority)
            };
        }

        public void SubscribeMessages(ISubscriber subscriber)
        {
            subscriber.Subscribe<SystemMessage.BecomeShuttingDown>(this);
            subscriber.Subscribe<SystemMessage.SystemInit>(this);
            subscriber.Subscribe<GossipMessage.GossipUpdated>(this);
            subscriber.Subscribe<ElectionMessage.StartElections>(this);
            subscriber.Subscribe<ElectionMessage.ElectionsTimedOut>(this);
            subscriber.Subscribe<ElectionMessage.ViewChange>(this);
            subscriber.Subscribe<ElectionMessage.ViewChangeProof>(this);
            subscriber.Subscribe<ElectionMessage.SendViewChangeProof>(this);
            subscriber.Subscribe<ElectionMessage.Prepare>(this);
            subscriber.Subscribe<ElectionMessage.PrepareOk>(this);
            subscriber.Subscribe<ElectionMessage.Proposal>(this);
            subscriber.Subscribe<ElectionMessage.Accept>(this);
        }

        public void Handle(SystemMessage.SystemInit _)
        {
            _publisher.Publish(TimerMessage.Schedule.Create(SendViewChangeProofInterval,
                _publisherEnvelope,
                new ElectionMessage.SendViewChangeProof()));
        }

        public void Handle(SystemMessage.BecomeShuttingDown message)
        {
            _state = ElectionsState.Shutdown;
        }

        public void Handle(GossipMessage.GossipUpdated message)
        {
            _servers = message.ClusterInfo.Members.Where(x => x.State != VNodeState.Manager)
                                                  .Where(x => x.IsAlive)
                                                  .OrderByDescending(x => x.InternalHttpEndPoint, IPComparer)
                                                  .ToArray();
        }

        public void Handle(ElectionMessage.StartElections message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (_state == ElectionsState.ElectingLeader) return;

#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.Elections_starting_elections();
#endif
            ShiftToLeaderElection(_lastAttemptedView + 1);
        }

        public void Handle(ElectionMessage.ElectionsTimedOut message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (message.View != _lastAttemptedView) return;
            // we are still on the same view, but we selected master
            if (_state != ElectionsState.ElectingLeader && _master is object) return;

#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.Elections_timed_out(message.View, _state, _master);
#endif
            ShiftToLeaderElection(_lastAttemptedView + 1);
        }

        private void ShiftToLeaderElection(int view)
        {
#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.Elections_shift_to_leader_election(view);
#endif

            _state = ElectionsState.ElectingLeader;
            _vcReceived.Clear();
            _prepareOkReceived.Clear();
            _lastAttemptedView = view;

            _masterProposal = null;
            _master = null;
            _acceptsReceived.Clear();

            var viewChangeMsg = new ElectionMessage.ViewChange(_nodeInfo.InstanceId, _nodeInfo.InternalHttp, view);
            Handle(viewChangeMsg);
            SendToAllExceptMe(viewChangeMsg);
            _publisher.Publish(TimerMessage.Schedule.Create(LeaderElectionProgressTimeout,
                                                            _publisherEnvelope,
                                                            new ElectionMessage.ElectionsTimedOut(view)));
        }

        private void SendToAllExceptMe(Message message)
        {
            foreach (var server in _servers.Where(x => x.InstanceId != _nodeInfo.InstanceId))
            {
                _publisher.Publish(new HttpMessage.SendOverHttp(server.InternalHttpEndPoint, message, DateTime.Now.Add(LeaderElectionProgressTimeout)));
            }
        }

        public void Handle(ElectionMessage.ViewChange message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (_state == ElectionsState.Idle) return;

            if (message.AttemptedView <= _lastInstalledView) return;

#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.Elections_viewchange_from(message);
#endif

            if (message.AttemptedView > _lastAttemptedView)
            {
                ShiftToLeaderElection(message.AttemptedView);
            }

            if (_vcReceived.Add(message.ServerId) && _vcReceived.Count == _clusterSize / 2 + 1)
            {
#if DEBUG
                if (Log.IsDebugLevelEnabled()) Log.ElectionsMajority_of_viewchange(message.AttemptedView);
#endif
                if (AmILeaderOf(_lastAttemptedView)) { ShiftToPreparePhase(); }
            }
        }

        public void Handle(ElectionMessage.SendViewChangeProof message)
        {
            if (_state == ElectionsState.Shutdown) return;

            if (_lastInstalledView >= 0)
                SendToAllExceptMe(new ElectionMessage.ViewChangeProof(_nodeInfo.InstanceId, _nodeInfo.InternalHttp, _lastInstalledView));

            _publisher.Publish(TimerMessage.Schedule.Create(SendViewChangeProofInterval,
                                                            _publisherEnvelope,
                                                            new ElectionMessage.SendViewChangeProof()));
        }

        public void Handle(ElectionMessage.ViewChangeProof message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (_state == ElectionsState.Idle) return;
            if (message.InstalledView <= _lastInstalledView) return;

            _lastAttemptedView = message.InstalledView;

            _publisher.Publish(TimerMessage.Schedule.Create(LeaderElectionProgressTimeout,
                                                            _publisherEnvelope,
                                                            new ElectionMessage.ElectionsTimedOut(_lastAttemptedView)));

            if (AmILeaderOf(_lastAttemptedView))
            {
#if DEBUG
                if (Log.IsDebugLevelEnabled()) { Log.ElectionsViewchangeproof_From_jumping_to_lead1er_state(message); }
#endif

                ShiftToPreparePhase();
            }
            else
            {
#if DEBUG
                if (Log.IsDebugLevelEnabled()) { Log.ElectionsViewchangeproof_From_jumping_to_non_leader_state(message); }
#endif

                ShiftToRegNonLeader();
            }
        }

        private bool AmILeaderOf(int lastAttemptedView)
        {
            var leader = _servers[lastAttemptedView % _servers.Length];
            return leader.InstanceId == _nodeInfo.InstanceId;
        }

        private void ShiftToPreparePhase()
        {
#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.ElectionsShiftToPreparePhase(_lastAttemptedView);
#endif

            _lastInstalledView = _lastAttemptedView;
            _prepareOkReceived.Clear();

            Handle(CreatePrepareOk(_lastInstalledView));
            SendToAllExceptMe(new ElectionMessage.Prepare(_nodeInfo.InstanceId, _nodeInfo.InternalHttp, _lastInstalledView));
        }

        public void Handle(ElectionMessage.Prepare message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (message.ServerId == _nodeInfo.InstanceId) return;
            if (message.View != _lastAttemptedView) return;
            if (_servers.All(x => x.InstanceId != message.ServerId)) return; // unknown instance

#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.ElectionsPrepareFrom(_lastAttemptedView, message);
#endif

            if (_state == ElectionsState.ElectingLeader) // install the view
            {
                ShiftToRegNonLeader();
            }

            var prepareOk = CreatePrepareOk(message.View);
            _publisher.Publish(new HttpMessage.SendOverHttp(message.ServerInternalHttp, prepareOk, DateTime.Now.Add(LeaderElectionProgressTimeout)));
        }

        private ElectionMessage.PrepareOk CreatePrepareOk(int view)
        {
            var ownInfo = GetOwnInfo();
            return new ElectionMessage.PrepareOk(view, ownInfo.InstanceId, ownInfo.InternalHttp,
                                                 ownInfo.EpochNumber, ownInfo.EpochPosition, ownInfo.EpochId,
                                                 ownInfo.LastCommitPosition, ownInfo.WriterCheckpoint, ownInfo.ChaserCheckpoint,
                                                 ownInfo.NodePriority);
        }

        private void ShiftToRegNonLeader()
        {
#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.ElectionsShiftToReg_Nonleader(_lastAttemptedView);
#endif

            _state = ElectionsState.NonLeader;
            _lastInstalledView = _lastAttemptedView;
        }

        public void Handle(ElectionMessage.PrepareOk msg)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (_state != ElectionsState.ElectingLeader) return;
            if (msg.View != _lastAttemptedView) return;

#if DEBUG
            if (Log.IsDebugLevelEnabled()) { Log.ElectionsPrepare_okFrom(msg); }
#endif

            if (!_prepareOkReceived.ContainsKey(msg.ServerId))
            {
                _prepareOkReceived.Add(msg.ServerId, msg);
                if (_prepareOkReceived.Count == _clusterSize / 2 + 1)
                {
                    ShiftToRegLeader();
                }
            }
        }

        private void ShiftToRegLeader()
        {
#if DEBUG
            if (Log.IsDebugLevelEnabled()) Log.ElectionsShiftToRegLeader(_lastAttemptedView);
#endif

            _state = ElectionsState.Leader;
            SendProposal();
        }

        private void SendProposal()
        {
            _acceptsReceived.Clear();
            _masterProposal = null;

            var master = GetBestMasterCandidate();
            if (master is null)
            {
#if DEBUG
                if (Log.IsTraceLevelEnabled()) Log.NomasterCandidateWhenTryingToSendProposal(_lastAttemptedView);
#endif
                return;
            }

            _masterProposal = master;

#if DEBUG
            if (Log.IsDebugLevelEnabled())
            {
                Log.ElectionsSendingProposalCandidate(_lastAttemptedView, master, this);
            }
#endif

            var proposal = new ElectionMessage.Proposal(_nodeInfo.InstanceId, _nodeInfo.InternalHttp,
                                                        master.InstanceId, master.InternalHttp,
                                                        _lastInstalledView,
                                                        master.EpochNumber, master.EpochPosition, master.EpochId,
                                                        master.LastCommitPosition, master.WriterCheckpoint, master.ChaserCheckpoint);
            Handle(new ElectionMessage.Accept(_nodeInfo.InstanceId, _nodeInfo.InternalHttp,
                                              master.InstanceId, master.InternalHttp, _lastInstalledView));
            SendToAllExceptMe(proposal);
        }

        private MasterCandidate GetBestMasterCandidate()
        {
            if (_lastElectedMaster.HasValue)
            {
                if (_prepareOkReceived.TryGetValue(_lastElectedMaster.Value, out ElectionMessage.PrepareOk masterMsg))
                {
                    return new MasterCandidate(masterMsg.ServerId, masterMsg.ServerInternalHttp,
                                               masterMsg.EpochNumber, masterMsg.EpochPosition, masterMsg.EpochId,
                                               masterMsg.LastCommitPosition, masterMsg.WriterCheckpoint, masterMsg.ChaserCheckpoint,
                                               masterMsg.NodePriority);

                }
                var master = _servers.FirstOrDefault(x => x.IsAlive && x.InstanceId == _lastElectedMaster && x.State == VNodeState.Master);
                if (master is object)
                {
                    return new MasterCandidate(master.InstanceId, master.InternalHttpEndPoint,
                                               master.EpochNumber, master.EpochPosition, master.EpochId,
                                               master.LastCommitPosition, master.WriterCheckpoint, master.ChaserCheckpoint,
                                               master.NodePriority);
                }
            }
            var best = _prepareOkReceived.Values
                                         .OrderByDescending(x => x.EpochNumber)
                                         .ThenByDescending(x => x.LastCommitPosition)
                                         .ThenByDescending(x => x.WriterCheckpoint)
                                         .ThenByDescending(x => x.NodePriority)
                                         .ThenByDescending(x => x.ChaserCheckpoint)
                                         .ThenByDescending(x => x.ServerId)
                                         .FirstOrDefault();
            if (best is null) return null;
            return new MasterCandidate(best.ServerId, best.ServerInternalHttp,
                                       best.EpochNumber, best.EpochPosition, best.EpochId,
                                       best.LastCommitPosition, best.WriterCheckpoint, best.ChaserCheckpoint, best.NodePriority);
        }

        private bool IsLegitimateMaster(int view, IPEndPoint proposingServerEndPoint, Guid proposingServerId,
                                        MasterCandidate candidate)
        {
            var master = _servers.FirstOrDefault(x => x.IsAlive && x.InstanceId == _lastElectedMaster && x.State == VNodeState.Master);
            if (master is object)
            {
                if (candidate.InstanceId == master.InstanceId
                    || candidate.EpochNumber > master.EpochNumber
                    || (candidate.EpochNumber == master.EpochNumber && candidate.EpochId != master.EpochId))
                {
                    return true;
                }

#if DEBUG
                if (Log.IsDebugLevelEnabled())
                {
                    Log.ElectionsNotLegitimateMasterProposalFrom(view, proposingServerEndPoint, proposingServerId, candidate, master);
                }
#endif

                return false;
            }

            if (candidate.InstanceId == _nodeInfo.InstanceId) return true;

            var ownInfo = GetOwnInfo();
            if (!IsCandidateGoodEnough(candidate, ownInfo))
            {
#if DEBUG
                if (Log.IsDebugLevelEnabled())
                {
                    Log.ElectionsNotLegitimateMasterProposalFrom(view, proposingServerEndPoint, proposingServerId, candidate, ownInfo);
                }
#endif

                return false;
            }
            return true;
        }

        private bool IsCandidateGoodEnough(MasterCandidate candidate, MasterCandidate ownInfo)
        {
            if (candidate.EpochNumber != ownInfo.EpochNumber)
                return candidate.EpochNumber > ownInfo.EpochNumber;
            if (candidate.LastCommitPosition != ownInfo.LastCommitPosition)
                return candidate.LastCommitPosition > ownInfo.LastCommitPosition;
            if (candidate.WriterCheckpoint != ownInfo.WriterCheckpoint)
                return candidate.WriterCheckpoint > ownInfo.WriterCheckpoint;
            if (candidate.ChaserCheckpoint != ownInfo.ChaserCheckpoint)
                return candidate.ChaserCheckpoint > ownInfo.ChaserCheckpoint;
            return true;
        }

        public void Handle(ElectionMessage.Proposal message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (message.ServerId == _nodeInfo.InstanceId) return;
            if (_state != ElectionsState.NonLeader) return;
            if (message.View != _lastInstalledView) return;
            if (_servers.All(x => x.InstanceId != message.ServerId)) return;
            if (_servers.All(x => x.InstanceId != message.MasterId)) return;

            var candidate = new MasterCandidate(message.MasterId, message.MasterInternalHttp,
                                                message.EpochNumber, message.EpochPosition, message.EpochId,
                                                message.LastCommitPosition, message.WriterCheckpoint, message.ChaserCheckpoint, 0);
            if (!IsLegitimateMaster(message.View, message.ServerInternalHttp, message.ServerId, candidate))
            {
                return;
            }

#if DEBUG
            if (Log.IsDebugLevelEnabled())
            {
                Log.ElectionsProposalFrom(_lastAttemptedView, message, candidate, this);
            }
#endif

            if (_masterProposal is null)
            {
                _masterProposal = candidate;
                _acceptsReceived.Clear();
            }

            if (_masterProposal.InstanceId == message.MasterId)
            {
                // NOTE: proposal from other server is also implicit Accept from that server
                Handle(new ElectionMessage.Accept(message.ServerId, message.ServerInternalHttp,
                                                  message.MasterId, message.MasterInternalHttp, message.View));
                var accept = new ElectionMessage.Accept(_nodeInfo.InstanceId, _nodeInfo.InternalHttp,
                                                        message.MasterId, message.MasterInternalHttp, message.View);
                Handle(accept); // implicitly sent accept to ourselves
                SendToAllExceptMe(accept);
            }
        }

        public void Handle(ElectionMessage.Accept message)
        {
            if (_state == ElectionsState.Shutdown) return;
            if (message.View != _lastInstalledView) return;
            if (_masterProposal is null) return;
            if (_masterProposal.InstanceId != message.MasterId) return;

#if DEBUG
            if (Log.IsDebugLevelEnabled()) { Log.ElectionsAcceptFrom(message); }
#endif

            if (_acceptsReceived.Add(message.ServerId) && _acceptsReceived.Count == _clusterSize / 2 + 1)
            {
                var master = _servers.FirstOrDefault(x => x.InstanceId == _masterProposal.InstanceId);
                if (master is object)
                {
                    _master = _masterProposal.InstanceId;
                    if (Log.IsInformationLevelEnabled()) { Log.Elections_done_elected_master(message.View, _masterProposal, this); }
                    _lastElectedMaster = _master;
                    _publisher.Publish(new ElectionMessage.ElectionsDone(message.View, master));
                }
            }
        }

        internal MasterCandidate GetOwnInfo()
        {
            var lastEpoch = _epochManager.GetLastEpoch();
            var writerCheckpoint = _writerCheckpoint.Read();
            var chaserCheckpoint = _chaserCheckpoint.Read();
            var lastCommitPosition = _getLastCommitPosition();
            return new MasterCandidate(_nodeInfo.InstanceId, _nodeInfo.InternalHttp,
                                       lastEpoch is null ? -1 : lastEpoch.EpochNumber,
                                       lastEpoch is null ? -1 : lastEpoch.EpochPosition,
                                       lastEpoch is null ? Guid.Empty : lastEpoch.EpochId,
                                       lastCommitPosition, writerCheckpoint, chaserCheckpoint, _nodePriority);
        }

        internal static string FormatNodeInfo(MasterCandidate candidate)
        {
            return FormatNodeInfo(candidate.InternalHttp, candidate.InstanceId,
                                  candidate.LastCommitPosition, candidate.WriterCheckpoint, candidate.ChaserCheckpoint,
                                  candidate.EpochNumber, candidate.EpochPosition, candidate.EpochId);
        }

        internal static string FormatNodeInfo(IPEndPoint serverEndPoint, Guid serverId,
                                              long lastCommitPosition, long writerCheckpoint, long chaserCheckpoint,
                                              int epochNumber, long epochPosition, Guid epochId)
        {
            return string.Format("[{0},{1:B}](L={2},W={3},C={4},E{5}@{6}:{7:B})",
                                 serverEndPoint, serverId,
                                 lastCommitPosition, writerCheckpoint, chaserCheckpoint,
                                 epochNumber, epochPosition, epochId);
        }

        internal class MasterCandidate
        {
            public readonly Guid InstanceId;
            public readonly IPEndPoint InternalHttp;

            public readonly int EpochNumber;
            public readonly long EpochPosition;
            public readonly Guid EpochId;

            public readonly long LastCommitPosition;
            public readonly long WriterCheckpoint;
            public readonly long ChaserCheckpoint;

            public readonly int NodePriority;

            public MasterCandidate(Guid instanceId, IPEndPoint internalHttp,
                                     int epochNumber, long epochPosition, Guid epochId,
                                     long lastCommitPosition, long writerCheckpoint, long chaserCheckpoint,
                                     int nodePriority)
            {
                InstanceId = instanceId;
                InternalHttp = internalHttp;
                EpochNumber = epochNumber;
                EpochPosition = epochPosition;
                EpochId = epochId;
                LastCommitPosition = lastCommitPosition;
                WriterCheckpoint = writerCheckpoint;
                ChaserCheckpoint = chaserCheckpoint;
                NodePriority = nodePriority;
            }
        }
    }
}
