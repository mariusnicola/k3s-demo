using System.Text;
using Xunit;

public class MachineBearerTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static string Jwt(DateTimeOffset expires)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Part("{}")}.{Part($"{{\"exp\":{expires.ToUnixTimeSeconds()}}}")}.signature";
    }

    private sealed class Issuer(TimeSpan lifetime, Clock clock)
    {
        public int SignIns;

        public async Task<string> Issue(CancellationToken token)
        {
            await Task.Yield();
            Interlocked.Increment(ref SignIns);
            return Jwt(clock.Now + lifetime);
        }
    }

    [Fact]
    public async Task Checks_reuse_one_token_until_it_nears_expiry()
    {
        var clock = new Clock(Start);
        var issuer = new Issuer(TimeSpan.FromHours(8), clock);
        var bearer = new MachineBearer(issuer.Issue, clock);

        var first = await bearer.Get(CancellationToken.None);
        clock.Now = Start.AddHours(7);
        var second = await bearer.Get(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, issuer.SignIns);
    }

    [Fact]
    public async Task A_token_near_expiry_is_replaced()
    {
        var clock = new Clock(Start);
        var issuer = new Issuer(TimeSpan.FromHours(8), clock);
        var bearer = new MachineBearer(issuer.Issue, clock);

        await bearer.Get(CancellationToken.None);
        clock.Now = Start.AddHours(8).AddSeconds(-30);
        await bearer.Get(CancellationToken.None);

        Assert.Equal(2, issuer.SignIns);
    }

    [Fact]
    public async Task Checks_running_together_share_one_sign_in()
    {
        var clock = new Clock(Start);
        var issuer = new Issuer(TimeSpan.FromHours(8), clock);
        var bearer = new MachineBearer(issuer.Issue, clock);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => bearer.Get(CancellationToken.None)));

        Assert.Equal(1, issuer.SignIns);
    }

    [Fact]
    public async Task A_failed_sign_in_is_tried_again_by_the_next_check()
    {
        var clock = new Clock(Start);
        var calls = 0;
        var bearer = new MachineBearer(_ => ++calls == 1 ? throw new InvalidOperationException("refused") : Task.FromResult(Jwt(Start.AddHours(8))), clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.Get(CancellationToken.None));
        await bearer.Get(CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_token_with_no_expiry_stops_with_a_clear_message()
    {
        var clock = new Clock(Start);
        var bearer = new MachineBearer(_ => Task.FromResult("not-a-jwt"), clock);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.Get(CancellationToken.None));

        Assert.Contains("expiry", failure.Message);
    }
}
