﻿using System;
using System.Net;

namespace EventStore.ClientAPI.SystemData
{
    internal sealed class InspectionResult
    {
        public readonly InspectionDecision Decision;
        public readonly string Description;
        public readonly IPEndPoint TcpEndPoint;
        public readonly IPEndPoint SecureTcpEndPoint;

        public InspectionResult(InspectionDecision decision, string description, IPEndPoint tcpEndPoint = null, IPEndPoint secureTcpEndPoint = null)
        {
            if (decision == InspectionDecision.Reconnect)
            {
                if (tcpEndPoint is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.tcpEndPoint); }
            }
            else
            {
                if (tcpEndPoint is object) { CoreThrowHelper.ThrowArgumentException_TcpEndPointIsNotNullForDecision(decision); }
            }

            Decision = decision;
            Description = description;
            TcpEndPoint = tcpEndPoint;
            SecureTcpEndPoint = secureTcpEndPoint;
        }
    }
}