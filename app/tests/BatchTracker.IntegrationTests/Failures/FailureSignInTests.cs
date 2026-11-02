using System.Net;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>A sign-in proves nothing about who failed before it, because one address can hold many clients.</summary>
public class FailureSignInTests(Stack stack) : FailureTest(stack)
{
    private const string Address = "203.0.113.20";

    [Theory]
    [InlineData(Env.LineMachineToken)]
    [InlineData(Env.MonitorMachineToken)]
    public async Task REQ_BT_049_A_machine_sign_in_clears_no_count_on_its_address(string token)
    {
        var host = Api();
        var client = From(host, Address);
        for (var i = 0; i < 4; i++) await client.GetAsync("/api/summary");
        using var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
        signIn.Headers.Add(MachineCredential.Header, token);

        var (status, took) = await TimeAsync(() => From(host, Address).SendAsync(signIn));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(took < Step, $"{took}");
        Assert.Equal(4, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_The_next_failure_after_a_sign_in_keeps_its_delay()
    {
        var host = Api();
        var client = From(host, Address);
        for (var i = 0; i < 4; i++) await client.GetAsync("/api/summary");
        await Login(From(host, Address), "ada");

        var (status, took) = await TimeAsync(() => client.GetAsync("/api/summary"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.True(took >= 4 * Step - Timing.Tick, $"{took}");
    }
}
