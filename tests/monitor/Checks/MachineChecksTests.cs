using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>B-009: the two ingress checks together, against a stand-in that refuses what the app refuses.</summary>
public class MachineChecksTests
{
    private const string Good = "monitor-token";

    private static string Bearer()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Part("{}")}.{Part($"{{\"exp\":{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()}}}")}.signature";
    }

    private sealed class App
    {
        public int SignIns;
        public string LineAnswer = "application/problem+json";
        public int LineStatus = 403;
        public string Figures = """{"sent":0,"items":0,"failedItems":0,"failedRequests":0,"lastItems":0,"lastMs":0,"lastFailure":null,"failing":false}""";
        public int FiguresStatus = 200;
        public string FiguresType = "application/json";

        public void Map(WebApplication app)
        {
            var bearer = Bearer();
            app.UseWebSockets();
            app.MapPost("/api/auth/machine", (HttpRequest request) =>
            {
                Interlocked.Increment(ref SignIns);
                return request.Headers["X-Machine-Token"] == Good ? Results.Ok(new { token = bearer }) : Results.StatusCode(401);
            });
            app.MapGet("/line", async context =>
            {
                context.Response.StatusCode = context.Request.Headers.Authorization == $"Bearer {bearer}" ? LineStatus : 401;
                context.Response.ContentType = LineAnswer;
                await context.Response.WriteAsync("{}");
            });
            MapFigures(this, app, bearer);
            app.Map("/ws/hops", async context =>
            {
                if (context.Request.Headers.Authorization != $"Bearer {bearer}")
                {
                    context.Response.StatusCode = 401;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.ReceiveAsync(new byte[16], context.RequestAborted);
            });
        }
    }

    private static void MapFigures(App stand, WebApplication app, string bearer) =>
        app.MapGet("/line/bundles", async context =>
        {
            context.Response.StatusCode = context.Request.Headers.Authorization == $"Bearer {bearer}" ? stand.FiguresStatus : 401;
            context.Response.ContentType = stand.FiguresType;
            await context.Response.WriteAsync(stand.Figures);
        });

    private static async Task<(WebApplication Host, MachineChecks Checks)> Start(App stand, string token = Good)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var host = builder.Build();
        stand.Map(host);
        await host.StartAsync();
        var origin = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (host, new MachineChecks(origin, token, TimeProvider.System));
    }

    [Fact]
    public async Task T_010_line_answers_403_and_hops_upgrades_on_one_sign_in()
    {
        var stand = new App();
        var (host, checks) = await Start(stand);
        await using var _ = host;

        await Task.WhenAll(checks.Line(CancellationToken.None), checks.Hops(CancellationToken.None));
        await checks.Line(CancellationToken.None);
        await checks.Hops(CancellationToken.None);

        Assert.Equal(1, stand.SignIns);
    }

    [Fact]
    public async Task T_010_a_wrong_machine_token_reads_as_refused_on_both_checks()
    {
        var (host, checks) = await Start(new App(), token: "wrong");
        await using var _ = host;

        Assert.Contains("machine sign-in refused", (await Assert.ThrowsAsync<InvalidOperationException>(() => checks.Line(CancellationToken.None))).Message);
        Assert.Contains("machine sign-in refused", (await Assert.ThrowsAsync<InvalidOperationException>(() => checks.Hops(CancellationToken.None))).Message);
    }

    [Fact]
    public async Task T_010_a_lost_line_route_reads_as_dropped()
    {
        var (host, checks) = await Start(new App { LineStatus = 200, LineAnswer = "text/html" });
        await using var _ = host;

        Assert.Contains("route dropped", (await Assert.ThrowsAsync<InvalidOperationException>(() => checks.Line(CancellationToken.None))).Message);
    }

    [Fact]
    public async Task REQ_BT_031_An_idle_line_passes_line_bundles()
    {
        var (host, checks) = await Start(new App());
        await using var _ = host;

        await checks.Bundles(CancellationToken.None);
    }

    [Fact]
    public async Task REQ_BT_031_A_failed_bundle_post_fails_line_bundles_and_names_it()
    {
        var stand = new App
        {
            Figures = """{"sent":9,"items":90,"failedItems":10,"failedRequests":2,"lastItems":10,"lastMs":40,"lastFailure":"HTTP 503","failing":true}"""
        };
        var (host, checks) = await Start(stand);
        await using var _ = host;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => checks.Bundles(CancellationToken.None));

        Assert.Contains("HTTP 503", failure.Message);
        Assert.Contains("10 items", failure.Message);
        Assert.Contains("2 of 9 posts", failure.Message);
    }

    [Fact]
    public async Task REQ_BT_031_No_answer_from_the_line_fails_line_bundles()
    {
        var (host, checks) = await Start(new App { FiguresStatus = 503 });
        await using var _ = host;

        Assert.Contains("answered 503", (await Assert.ThrowsAsync<InvalidOperationException>(() => checks.Bundles(CancellationToken.None))).Message);
    }

    [Fact]
    public async Task REQ_BT_031_A_lost_bundles_route_reads_as_dropped()
    {
        var (host, checks) = await Start(new App { FiguresType = "text/html" });
        await using var _ = host;

        Assert.Contains("route dropped", (await Assert.ThrowsAsync<InvalidOperationException>(() => checks.Bundles(CancellationToken.None))).Message);
    }

    [Fact]
    public void T_010_the_report_loop_runs_these_checks_and_no_anonymous_one()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "deploy", "monitor", "Checks", "ReportChecks.cs"));

        Assert.Contains("Run(\"ingress /line\", machine.Line)", program);
        Assert.Contains("Run(\"line bundles\", machine.Bundles)", program);
        Assert.Contains("Run(\"ingress /ws/hops\", machine.Hops)", program);
        Assert.DoesNotContain("BrowserPaths.Unauthorized", program);
    }

    private static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));
}
