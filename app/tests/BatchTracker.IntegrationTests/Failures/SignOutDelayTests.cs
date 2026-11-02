using System.Net;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>Signing out twice guesses nothing, so it is no failed sign-in. A credential sent to the route still is one.</summary>
public class SignOutDelayTests(Stack stack) : FailureTest(stack)
{
    private const string Address = "203.0.113.30";

    [Fact]
    public async Task T_017_A_repeated_sign_out_spends_no_failure()
    {
        var host = Api();
        var client = From(host, Address);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Equal(0, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task T_017_A_bad_bearer_on_sign_out_still_counts()
    {
        var host = Api();

        var reply = await From(host, Address, bearer: "guess").PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Equal(1, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task T_017_A_bad_cookie_on_sign_out_still_counts()
    {
        var host = Api();
        var client = From(host, Address);
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie.Name}=guess");
        client.DefaultRequestHeaders.Add("Origin", Env.Origin);

        var reply = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Equal(1, LedgerOf(host).FailuresOf(Address));
    }
}
