using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>What the tests of FailureDelayTests and its neighbours share.</summary>
public abstract class FailureDelayTest(Stack stack) : FailureTest(stack)
{
    /// <summary>Timers run on the coarse system tick and the stopwatch does not, so a delay can read up to one tick short.</summary>
    protected static bool Held(TimeSpan took, int steps) => took >= steps * Step - Timing.Tick;

    protected sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
