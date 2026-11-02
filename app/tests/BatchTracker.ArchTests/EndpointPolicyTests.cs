using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using BatchTracker.Live;
using BatchTracker.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BatchTracker.ArchTests;

/// <summary>Reads what the running host actually mapped, so a new route cannot miss these rules.</summary>
public class EndpointPolicyTests : EndpointPolicyTest
{
    [Fact]
    public void REQ_BT_050_Api_routes_are_all_limited()
    {
        Assert.Empty(RateExempt(RoutesOf<ApiMarker>()));
        Assert.Equal(
            ["GET /api/healthz", "GET /api/ready", "GET /healthz", "GET /metrics"],
            InHealthBucket(RoutesOf<ApiMarker>()));
    }

    [Fact]
    public void T_013_Every_route_has_plain_words_for_where()
    {
        Assert.All(EveryRoute(), r => Assert.NotEqual(ProblemPlaces.Unknown, ProblemPlaces.Of(r.Method, Filled(r.Path))));
    }

    [Fact]
    public void B_014_Every_writing_route_says_what_a_500_did_to_a_batch()
    {
        var expected = new Dictionary<string, string>
        {
            ["POST /api/auth/dev-login"] = ProblemWords.NoBatch,
            ["POST /api/browser-reports"] = ProblemWords.NoBatch,
            ["POST /api/auth/logout"] = ProblemWords.NoBatch,
            ["POST /api/auth/machine"] = ProblemWords.NoBatch,
            ["POST /api/batches"] = ProblemWords.MayHaveChanged,
            ["POST /api/batches/bundle"] = ProblemWords.MayHaveChanged,
            ["POST /api/batches/farm-a/b1/advance"] = ProblemWords.MayHaveChanged,
            ["POST /api/batches/farm-a/b1/fail-next"] = ProblemWords.MayHaveChanged,
            ["POST /api/batches/farm-a/b1/inspect"] = ProblemWords.MayHaveChanged,
            ["POST /api/batches/farm-a/b1/replay"] = ProblemWords.MayHaveChanged,
            ["POST /api/compliance/run"] = ProblemWords.FlagsMayHaveChanged,
            ["POST /events"] = ProblemWords.MayHaveChanged,
            ["POST /nightly/run"] = ProblemWords.FlagsMayHaveChanged,
            ["POST /reports"] = ProblemWords.NoBatch,
            ["POST /hops"] = ProblemWords.NoBatch,
            ["POST /changes"] = ProblemWords.NoBatch,
            ["POST /logs"] = ProblemWords.NoBatch,
            ["POST /line"] = ProblemWords.NoBatch,
            ["DELETE /line"] = ProblemWords.NoBatch
        };

        var writes = EveryRoute().Where(r => r.Method != "GET" && r.Method != "ANY").DistinctBy(r => (r.Method, r.Path)).ToList();

        Assert.Equal(expected.Keys.Order(), writes.Select(r => $"{r.Method} {Filled(r.Path)}").Order());
        Assert.All(writes, r => Assert.Equal(expected[$"{r.Method} {Filled(r.Path)}"], ProblemWords.ImpactOf(r.Method, Filled(r.Path), 500)));
    }

    [Fact]
    public void REQ_BT_050_Live_routes_are_all_limited()
    {
        Assert.Empty(RateExempt(RoutesOf<LiveMarker>()));
        Assert.Equal(["POST /changes", "POST /hops", "POST /logs", "POST /reports"], Names(RoutesOf<LiveMarker>().Where(r => r.MachineBucket)));
        Assert.Equal(["GET /healthz", "GET /ready"], InHealthBucket(RoutesOf<LiveMarker>()));
    }

    [Fact]
    public void REQ_BT_050_Line_routes_are_all_limited()
    {
        Assert.Empty(RateExempt(RoutesOf<LineMarker>()));
        Assert.Equal(["GET /healthz", "GET /ready"], InHealthBucket(RoutesOf<LineMarker>()));
    }

    [Fact]
    public void REQ_BT_050_Every_Worker_and_Dispatcher_route_has_a_bucket()
    {
        Assert.Equal(["GET /healthz", "GET /metrics", "GET /ready"], InHealthBucket(RoutesOf<WorkerMarker>()));
        Assert.Equal(["POST /events", "POST /nightly/run"], Names(RoutesOf<WorkerMarker>().Where(r => r.MachineBucket)));
        Assert.Equal(["GET /healthz", "GET /metrics", "GET /status"], InHealthBucket(RoutesOf<DispatcherMarker>()));
    }

    [Fact]
    public void REQ_BT_049_Failure_delays_run_after_the_limiter_and_before_authorization()
    {
        foreach (var service in new[] { "Api", "Live", "Line" })
        {
            var program = File.ReadAllText(Path.Combine(Root, "src", $"BatchTracker.{service}", "Program.cs"));
            var delays = program.IndexOf("app.UseFailureDelays()", StringComparison.Ordinal);

            Assert.True(delays > program.IndexOf("app.UseRateLimiter()", StringComparison.Ordinal), service);
            Assert.True(delays < program.IndexOf("app.UseAuthorization()", StringComparison.Ordinal), service);
        }
    }

    [Fact]
    public void REQ_BT_050_Limiter_runs_after_authentication_and_before_authorization()
    {
        foreach (var service in new[] { "Api", "Live", "Line" })
        {
            var program = File.ReadAllText(Path.Combine(Root, "src", $"BatchTracker.{service}", "Program.cs"));
            var limiter = program.IndexOf("app.UseRateLimiter()", StringComparison.Ordinal);

            Assert.True(limiter > program.IndexOf("app.UseAuthentication()", StringComparison.Ordinal), service);
            Assert.True(limiter < program.IndexOf("app.UseAuthorization()", StringComparison.Ordinal), service);
        }
    }

    [Fact]
    public void REQ_BT_014_Every_api_route_declares_a_policy()
    {
        EveryRouteDeclaresAccess(RoutesOf<ApiMarker>());
    }

    [Fact]
    public void REQ_BT_014_Only_login_and_health_routes_of_the_api_are_anonymous()
    {
        var anonymous = RoutesOf<ApiMarker>().Where(r => r.Anonymous);

        Assert.Equal(
            ["GET /api/healthz", "GET /api/ready", "GET /api/ready/deep", "GET /api/session", "GET /healthz", "GET /metrics", "POST /api/auth/dev-login", "POST /api/auth/machine", "POST /api/browser-reports"],
            Names(anonymous));
    }

    [Theory]
    [InlineData("GET", "/api/batches", "batch.read")]
    [InlineData("POST", "/api/batches", "batch.write")]
    [InlineData("GET", "/api/batches/{farmId}/{id}", "batch.read")]
    [InlineData("POST", "/api/batches/bundle", "batch.write")]
    [InlineData("POST", "/api/batches/{farmId}/{id}/advance", "batch.write")]
    [InlineData("POST", "/api/batches/{farmId}/{id}/inspect", "batch.inspect")]
    [InlineData("POST", "/api/batches/{farmId}/{id}/fail-next", "ops.admin")]
    [InlineData("POST", "/api/batches/{farmId}/{id}/replay", "ops.admin")]
    [InlineData("GET", "/api/summary", "batch.read")]
    [InlineData("GET", "/api/alerts", "batch.read")]
    [InlineData("POST", "/api/compliance/run", "ops.admin")]
    public void REQ_BT_014_Api_route_carries_the_contract_permission(string method, string path, string policy)
    {
        var route = Assert.Single(RoutesOf<ApiMarker>(), r => r.Method == method && r.Path == path);

        Assert.Contains(policy, route.Policies);
    }

    [Fact]
    public void REQ_BT_014_Worker_routes_need_the_internal_token_except_healthz()
    {
        var routes = RoutesOf<WorkerMarker>();

        EveryRouteDeclaresAccess(routes);
        Assert.Equal(["GET /healthz", "GET /metrics"], Names(routes.Where(r => r.Anonymous)));
        Assert.Equal(
            ["GET /ready", "POST /events", "POST /nightly/run"],
            Names(routes.Where(r => r.Policies.Contains(AuthSetup.InternalPolicy))));
    }

    [Fact]
    public void REQ_BT_014_Ws_hops_needs_a_jwt_and_the_rest_of_live_is_named()
    {
        var routes = RoutesOf<LiveMarker>();

        EveryRouteDeclaresAccess(routes);
        Assert.False(Assert.Single(routes, r => r.Path == "/ws/hops").Anonymous);
        Assert.Equal(["GET /healthz", "GET /ready"], Names(routes.Where(r => r.Anonymous)));
        Assert.Equal(
            ["POST /changes", "POST /hops", "POST /logs", "POST /reports"],
            Names(routes.Where(r => r.Policies.Contains(AuthSetup.InternalPolicy))));
    }

    [Fact]
    public void REQ_BT_014_Line_routes_need_ops_admin_except_healthz_and_bundle_figures()
    {
        var routes = RoutesOf<LineMarker>();

        EveryRouteDeclaresAccess(routes);
        Assert.Equal(["GET /healthz", "GET /ready"], Names(routes.Where(r => r.Anonymous)));
        Assert.DoesNotContain("ops.admin", Assert.Single(routes, r => r.Path == "/line/bundles").Policies);
        Assert.All(routes.Where(r => !r.Anonymous && r.Path != "/line/bundles"), r => Assert.Contains("ops.admin", r.Policies));
    }

    [Fact]
    public void REQ_BT_014_Dispatcher_exposes_only_health_and_status()
    {
        Assert.Equal(["GET /healthz", "GET /metrics", "GET /status"], Names(RoutesOf<DispatcherMarker>()));
    }
}
