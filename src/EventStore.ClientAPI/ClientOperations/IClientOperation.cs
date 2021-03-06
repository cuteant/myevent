﻿using System;
using EventStore.ClientAPI.SystemData;
using EventStore.Transport.Tcp.Messages;

namespace EventStore.ClientAPI.ClientOperations
{
    internal interface IClientOperation
    {
        TcpPackage CreateNetworkPackage(Guid correlationId);
        InspectionResult InspectPackage(TcpPackage package);
        void Fail(Exception exception);
    }

    internal interface ISubscription: IClientOperation
    {
        
    }
}