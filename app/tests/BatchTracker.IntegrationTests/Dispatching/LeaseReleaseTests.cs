using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using k8s.LeaderElection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

public class LeaseReleaseTests
{
    private sealed class FakeLock(string holder) : ILock
    {
        public string Identity => "me";
        public int Updates;
        public LeaderElectionRecord? Written;

        public Task<LeaderElectionRecord> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LeaderElectionRecord { HolderIdentity = Written?.HolderIdentity ?? holder, LeaseDurationSeconds = 10, LeaderTransitions = 4 });

        public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken = default)
        {
            Updates++;
            Written = record;
            return Task.FromResult(true);
        }

        public string Describe() => "fake";
    }

    [Fact]
    public async Task REQ_BT_050_Leader_gives_the_lease_up_so_the_next_pod_need_not_wait()
    {
        var lease = new FakeLock("me");

        await LeaseRelease.ReleaseAsync(lease, default);

        Assert.Equal("", lease.Written!.HolderIdentity);
        Assert.Equal(4, lease.Written.LeaderTransitions);
    }

    [Fact]
    public async Task REQ_BT_050_A_pod_that_does_not_lead_leaves_the_lease_alone()
    {
        var lease = new FakeLock("other");

        await LeaseRelease.ReleaseAsync(lease, default);

        Assert.Equal(0, lease.Updates);
    }

    [Fact]
    public async Task REQ_BT_050_Stop_arriving_twice_releases_once()
    {
        var lease = new FakeLock("me");

        await LeaseRelease.ReleaseAsync(lease, default);
        await LeaseRelease.ReleaseAsync(lease, default);

        Assert.Equal(1, lease.Updates);
    }
}
