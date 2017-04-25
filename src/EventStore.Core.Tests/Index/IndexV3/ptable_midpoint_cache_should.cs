﻿using System;
using Microsoft.Extensions.Logging;
using EventStore.Core.Index;
using NUnit.Framework;

namespace EventStore.Core.Tests.Index.IndexV3
{
    public class ptable_midpoint_cache_should: IndexV1.ptable_midpoint_cache_should
    {
        public ptable_midpoint_cache_should()
        {
            _ptableVersion = PTableVersions.IndexV3;
        }
    }
}