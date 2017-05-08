﻿using System;

namespace EventStore.ClientAPI.Rx
{
  public static class EventStoreConnectionSettings
  {
    public static readonly ConnectionSettingsBuilder Default = ConnectionSettings.Create()
                                                                                 .SetReconnectionDelayTo(TimeSpan.FromSeconds(1))
                                                                                 .KeepReconnecting();
  }
}