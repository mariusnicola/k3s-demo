using System.Net;
using System.Net.Http.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>From the tenth failure a client is refused, one request at a time, but a right credential still gets in.</summary>
public class RefusalTests(Stack stack) : FailureTest(stack)
{
    private const string Address = "203.0.113.40";

    /// <summary>The real count on a clock that lets nine failures pass quickly.</summary>
    private static readonly FailureRules Quick = Short with { FirstDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(1) };

    private static async Task FailNineAsync(HttpClient client)
    {
        for (var i = 0; i < 9; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/summary")).StatusCode);
    }

    private static void AssertRefused(HttpResponseMessage reply)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, reply.StatusCode);
        Assert.True(int.Parse(reply.Headers.GetValues("Retry-After").Single()) > 30);
        Assert.Equal("application/problem+json", reply.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task REQ_BT_049_The_tenth_failure_one_at_a_time_is_a_429_and_spends_nothing()
    {
        var host = Api(Quick);
        var client = From(host, Address);
        await FailNineAsync(client);

        AssertRefused(await client.GetAsync("/api/summary"));

        Assert.Equal(9, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_right_sign_in_gets_past_a_refused_client()
    {
        var host = Api(Quick);
        var client = From(host, Address);
        await FailNineAsync(client);

        var reply = await Login(client, "ada");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.True(reply.Headers.Contains("Set-Cookie"));
        Assert.Contains("ada", await reply.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task REQ_BT_049_A_wrong_sign_in_from_a_refused_client_is_a_429_and_counts()
    {
        var host = Api(Quick);
        var client = From(host, Address);
        await FailNineAsync(client);

        AssertRefused(await Login(client, "nobody"));

        Assert.Equal(10, LedgerOf(host).FailuresOf(Address));
    }

    /// <summary>Delays grow past the quick start to one hold, and a refused client's guesses are held for it.</summary>
    private static readonly FailureRules Held = Short with { FirstDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(300) };

    [Fact]
    public async Task REQ_BT_049_A_wrong_sign_in_from_a_refused_client_holds_for_the_delay()
    {
        var host = Api(Held);
        var client = From(host, Address);
        await FailNineAsync(client);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        AssertRefused(await Login(client, "nobody"));

        Assert.True(clock.Elapsed >= Held.MaxDelay - Timing.Tick, $"{clock.Elapsed}");
    }

    [Fact]
    public async Task REQ_BT_049_A_right_sign_in_from_a_refused_client_answers_at_once()
    {
        var host = Api(Held);
        var client = From(host, Address);
        await FailNineAsync(client);

        var (status, took) = await TimeAsync(() => Login(client, "ada"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(took < Held.MaxDelay, $"{took}");
    }

    /// <summary>Ten seats held for the delay allow ten guesses per delay, however fast the client sends.</summary>
    [Fact]
    public async Task REQ_BT_049_Guessing_while_refused_runs_at_the_seat_rate()
    {
        var host = Api(Held);
        var client = From(host, Address);
        await FailNineAsync(client);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var guesses = new List<Task>();

        while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            guesses.Add(Login(client, "nobody"));
            await Task.Delay(5);
        }
        await Task.WhenAll(guesses);

        var limit = (long)(Held.MaxWaiting * Math.Ceiling(clock.Elapsed / Held.MaxDelay));
        Assert.True(guesses.Count > limit, $"{guesses.Count} against {limit}");
        Assert.InRange(LedgerOf(host).FailuresOf(Address) - 9, 10, limit);
    }

    /// <summary>A wrong guess whose client leaves during the hold.</summary>
    private static async Task HangUpAsync(HttpClient client)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        try
        {
            await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "nobody" }, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Leaving early is the point: it must not buy the guess any speed.
        }
    }

    /// <summary>A real listener with short delays and one sign-in route that always refuses.</summary>
    private static async Task<WebApplication> GuessHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddFailureDelays();
        builder.Services.RemoveAll<FailureRules>().AddSingleton(new FailureRules
        {
            FirstDelay = TimeSpan.FromMilliseconds(5),
            MaxDelay = TimeSpan.FromMilliseconds(500)
        });
        var app = builder.Build();
        app.UseFailureDelays();
        app.MapGet("/guess", () => Results.Unauthorized()).AllowAnonymous().CountsAsSignIn();
        await app.StartAsync();
        return app;
    }

    /// <summary>On a real listener, hanging up once the ledger shows the route has answered, so the guess is known to be inside its hold.</summary>
    [Fact]
    public async Task REQ_BT_049_A_refused_guesser_who_hangs_up_is_counted_once()
    {
        await using var app = await GuessHostAsync();
        var ledger = app.Services.GetRequiredService<FailureLedger>();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        for (var i = 0; i < 9; i++) await client.GetAsync("/guess");
        using var cancel = new CancellationTokenSource();

        var guess = client.GetAsync("/guess", cancel.Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (ledger.FailuresOf("127.0.0.1") < 10 && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(5);
        Assert.Equal(10, ledger.FailuresOf("127.0.0.1"));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guess);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(10, ledger.FailuresOf("127.0.0.1"));
    }

    [Fact]
    public async Task REQ_BT_049_Guessers_who_hang_up_run_at_the_seat_rate()
    {
        var host = Api(Held);
        var client = From(host, Address);
        await FailNineAsync(client);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;

        while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            await HangUpAsync(client);
            sent++;
        }

        var limit = (long)(Held.MaxWaiting * Math.Ceiling(clock.Elapsed / Held.MaxDelay));
        Assert.True(sent > limit, $"{sent} against {limit}");
        Assert.InRange(LedgerOf(host).FailuresOf(Address) - 9, 10, limit);
    }

    [Fact]
    public async Task REQ_BT_049_A_right_machine_token_gets_past_a_refused_client()
    {
        var host = Api(Quick);
        var client = From(host, Address);
        await FailNineAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
        request.Headers.Add(MachineCredential.Header, Env.LineMachineToken);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_049_A_bad_sign_in_body_keeps_its_own_answer()
    {
        var host = Api(Quick);
        var client = From(host, Address);
        await FailNineAsync(client);

        var reply = await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "" });

        Assert.NotEqual(HttpStatusCode.TooManyRequests, reply.StatusCode);
        Assert.Equal(9, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_signed_in_caller_on_a_refused_address_is_not_refused()
    {
        var host = Api(Quick);
        await FailNineAsync(From(host, Address));

        var reply = await From(host, Address, bearer: Sockets.TokenFor("ada")).GetAsync("/api/summary");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_049_Health_on_a_refused_address_is_not_refused()
    {
        var host = Api(Quick);
        var client = From(host, Address);
        await FailNineAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/healthz")).StatusCode);
    }
}
