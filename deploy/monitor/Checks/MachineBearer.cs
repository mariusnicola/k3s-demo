using System.Text.Json;

/// <summary>Keeps one token until it nears expiry: each sign-in clears the failure count of the address it came from.</summary>
public sealed class MachineBearer(Func<CancellationToken, Task<string>> signIn, TimeProvider time)
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private string? current;
    private DateTimeOffset expires;

    public async Task<string> Get(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (current is not null && time.GetUtcNow() < expires - Margin) return current;
            var fresh = await signIn(token);
            expires = ExpiryOf(fresh);
            current = fresh;
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    private static DateTimeOffset ExpiryOf(string jwt)
    {
        try
        {
            var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            var padded = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(padded));
            return DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("exp").GetInt64());
        }
        catch (Exception problem) when (problem is IndexOutOfRangeException or FormatException or JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException("machine token has no readable expiry");
        }
    }
}

/// <summary>The monitor's signed-in checks: they stay out of the failure ledger, which anonymous checks fed.</summary>
public sealed class MachineChecks(string origin, string machineToken, TimeProvider time)
{
    private readonly MachineBearer bearer = new(token => BrowserPaths.SignIn(origin, machineToken, token), time);

    public async Task Line(CancellationToken token) =>
        await BrowserPaths.Forbidden(origin, "/line", await bearer.Get(token), token);

    public async Task Bundles(CancellationToken token) =>
        await BrowserPaths.BundlesWorking(origin, await bearer.Get(token), token);

    public async Task Hops(CancellationToken token) =>
        await BrowserPaths.Upgraded(origin.Replace("http://", "ws://") + "/ws/hops", origin, await bearer.Get(token), token);
}
