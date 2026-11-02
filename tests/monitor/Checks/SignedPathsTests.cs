using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class SignedPathsTests
{
    private const string Html = "text/html";
    private const string Json = "application/json";

    private static async Task<(WebApplication App, string Origin)> Serve(Action<WebApplication> routes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        routes(app);
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    private static RequestDelegate Answer(int status, string type = Json, string body = "{}") => async context =>
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        await context.Response.WriteAsync(body);
    };

    private static async Task<string> Failure(Func<Task> check) =>
        (await Assert.ThrowsAsync<InvalidOperationException>(check)).Message;

    private static RequestDelegate Sends(params string[] frames) => async context =>
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        foreach (var frame in frames)
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(frame), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.ReceiveAsync(new byte[16], context.RequestAborted);
    };

    [Fact]
    public async Task OperationsOnly_passes_when_the_first_frame_is_operations()
    {
        var (app, origin) = await Serve(a => a.Map("/ws", Sends("{\"type\":\"operations\",\"operations\":[]}")));
        await using var _ = app;

        await BrowserPaths.OperationsOnly(origin.Replace("http://", "ws://") + "/ws", origin, CancellationToken.None);
    }

    [Fact]
    public async Task OperationsOnly_fails_naming_a_report_that_reached_an_anonymous_socket()
    {
        var (app, origin) = await Serve(a => a.Map("/ws", Sends("{\"caps\":[1]}", "{\"type\":\"operations\",\"operations\":[]}")));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.OperationsOnly(origin.Replace("http://", "ws://") + "/ws", origin, CancellationToken.None));

        Assert.Contains("report reached an anonymous socket", message);
    }

    [Fact]
    public async Task Report_sends_the_session_as_a_cookie_and_never_in_the_url()
    {
        string? cookie = null;
        string? query = null;
        var (app, origin) = await Serve(a => a.Map("/ws", async context =>
        {
            cookie = context.Request.Cookies["bt_session"];
            query = context.Request.QueryString.Value;
            await Sends("{\"caps\":[1]}")(context);
        }));
        await using var _ = app;

        await BrowserPaths.Report(origin.Replace("http://", "ws://") + "/ws", origin, "session-value", CancellationToken.None);

        Assert.Equal("session-value", cookie);
        Assert.Equal("", query);
    }

    [Fact]
    public async Task Sign_in_sends_the_token_in_the_machine_header_and_returns_the_bearer()
    {
        string? sent = null;
        var (app, origin) = await Serve(a => a.MapPost("/api/auth/machine", (HttpRequest request) =>
        {
            sent = request.Headers["X-Machine-Token"];
            return Results.Ok(new { token = "bearer-value" });
        }));
        await using var _ = app;

        Assert.Equal("bearer-value", await BrowserPaths.SignIn(origin, "machine-value", CancellationToken.None));
        Assert.Equal("machine-value", sent);
    }

    [Fact]
    public async Task A_refused_sign_in_reads_as_refused()
    {
        var (app, origin) = await Serve(a => a.MapPost("/api/auth/machine", Answer(401)));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.SignIn(origin, "wrong", CancellationToken.None));

        Assert.Contains("machine sign-in refused", message);
        Assert.Contains("401", message);
    }

    [Fact]
    public async Task A_sign_in_web_answered_reads_as_a_dropped_route()
    {
        var (app, origin) = await Serve(a => a.MapPost("/api/auth/machine", Answer(200, Html, "<html>")));
        await using var _ = app;

        Assert.Contains("route dropped", await Failure(() => BrowserPaths.SignIn(origin, "any", CancellationToken.None)));
    }

    [Fact]
    public async Task Forbidden_passes_on_403_and_sends_the_bearer()
    {
        string? sent = null;
        var (app, origin) = await Serve(a => a.MapGet("/line", async context =>
        {
            sent = context.Request.Headers.Authorization;
            await Answer(403)(context);
        }));
        await using var _ = app;

        await BrowserPaths.Forbidden(origin, "/line", "bearer-value", CancellationToken.None);

        Assert.Equal("Bearer bearer-value", sent);
    }

    [Fact]
    public async Task Forbidden_reads_html_200_as_a_dropped_route()
    {
        var (app, origin) = await Serve(a => a.MapGet("/line", Answer(200, Html, "<html>")));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.Forbidden(origin, "/line", "any", CancellationToken.None));

        Assert.Contains("route dropped", message);
        Assert.Contains("/line", message);
    }

    [Fact]
    public async Task Forbidden_reads_json_200_as_too_much_permission_not_a_dropped_route()
    {
        var (app, origin) = await Serve(a => a.MapGet("/line", Answer(200)));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.Forbidden(origin, "/line", "any", CancellationToken.None));

        Assert.DoesNotContain("route dropped", message);
        Assert.Contains("permission", message);
    }

    [Fact]
    public async Task Forbidden_reads_401_as_a_refused_bearer()
    {
        var (app, origin) = await Serve(a => a.MapGet("/line", Answer(401)));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.Forbidden(origin, "/line", "any", CancellationToken.None));

        Assert.DoesNotContain("route dropped", message);
        Assert.Contains("answered 401, expected 403", message);
    }

    [Fact]
    public async Task Upgraded_passes_on_101_with_the_bearer_and_the_origin()
    {
        string? bearer = null;
        string? sentOrigin = null;
        var (app, origin) = await Serve(a => a.Map("/ws/hops", async context =>
        {
            bearer = context.Request.Headers.Authorization;
            sentOrigin = context.Request.Headers.Origin;
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.ReceiveAsync(new byte[16], context.RequestAborted);
        }));
        await using var _ = app;

        await BrowserPaths.Upgraded(origin.Replace("http://", "ws://") + "/ws/hops", origin, "bearer-value", CancellationToken.None);

        Assert.Equal("Bearer bearer-value", bearer);
        Assert.Equal(origin, sentOrigin);
    }

    [Fact]
    public async Task Upgraded_reads_200_as_a_dropped_route()
    {
        var (app, origin) = await Serve(a => a.Map("/ws/hops", Answer(200, Html, "<html>")));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.Upgraded(origin.Replace("http://", "ws://") + "/ws/hops", origin, "any", CancellationToken.None));

        Assert.Contains("route dropped", message);
        Assert.Contains("/ws/hops", message);
    }

    [Fact]
    public async Task Upgraded_reads_401_as_a_refused_upgrade()
    {
        var (app, origin) = await Serve(a => a.Map("/ws/hops", Answer(401)));
        await using var _ = app;

        var message = await Failure(() => BrowserPaths.Upgraded(origin.Replace("http://", "ws://") + "/ws/hops", origin, "any", CancellationToken.None));

        Assert.DoesNotContain("route dropped", message);
        Assert.Contains("answered 401, expected 101", message);
    }
}
