using BatchTracker.Application;

namespace BatchTracker.Infrastructure;

/// <summary>What a person was doing when a request failed, in words, because a raw path means nothing to an operator.</summary>
public static class ProblemPlaces
{
    public const string Unknown = "Making this request";
    private const string AnyId = "*";
    private const string Farm = "{farm}";

    /// <summary>One row per route family. An architecture test fails when a route has no row.</summary>
    private static readonly (string Method, string Path, string Words)[] Places =
    [
        ("POST", "/api/auth/dev-login", "Signing in"),
        ("POST", "/api/auth/machine", "Signing in"),
        ("POST", "/api/auth/logout", "Signing out"),
        ("GET", "/api/me", "Reading who is signed in"),
        ("GET", "/api/session", "Reading who is signed in"),
        ("GET", "/api/batches", "Reading the batch list"),
        ("POST", "/api/batches", "Creating a batch"),
        ("POST", "/api/batches/bundle", "Sending a bundle of batch steps"),
        ("GET", "/api/batches/{farm}/*", "Reading a batch on {farm}"),
        ("POST", "/api/batches/{farm}/*/advance", "Moving a batch on {farm}"),
        ("POST", "/api/batches/{farm}/*/inspect", "Recording an inspection on {farm}"),
        ("POST", "/api/batches/{farm}/*/fail-next", "Setting a batch on {farm} to fail its next event"),
        ("POST", "/api/batches/{farm}/*/replay", "Replaying a blocked batch on {farm}"),
        ("GET", "/api/summary", "Reading the summary"),
        ("GET", "/api/alerts", "Reading the alerts"),
        ("POST", "/api/compliance/run", "Running the compliance check"),
        ("POST", "/api/browser-reports", "Reporting a browser failure"),
        ("POST", "/nightly/run", "Running the nightly compliance check"),
        ("POST", "/events", "Recording a batch event"),
        ("GET", "/api/ready", "Checking the service is ready"),
        ("GET", "/api/ready/deep", "Checking the service is ready"),
        ("GET", "/ready", "Checking the service is ready"),
        ("GET", "/api/healthz", "Checking the service is up"),
        ("GET", "/healthz", "Checking the service is up"),
        ("GET", "/metrics", "Reading the metrics"),
        ("GET", "/status", "Reading the Dispatcher status"),
        ("GET", "/line", "Reading the Line status"),
        ("POST", "/line", "Starting the Line"),
        ("DELETE", "/line", "Stopping the Line"),
        ("GET", "/line/logs", "Reading the Line log"),
        ("GET", "/line/bundles", "Reading the Line bundle figures"),
        ("GET", "/line/ws", "Opening the Line feed"),
        ("GET", "/live/logs", "Reading the service log"),
        ("GET", "/ws/hops", "Opening the live feed"),
        ("POST", "/reports", "Reporting to Live"),
        ("POST", "/hops", "Reporting to Live"),
        ("POST", "/changes", "Reporting to Live"),
        ("POST", "/logs", "Reporting to Live")
    ];

    /// <summary>The `where` words hold only a farm the service knows. The reply's `path` and a bad-request detail can still carry the caller's text.</summary>
    public static string Of(string method, string path)
    {
        var sent = Segments(path);
        foreach (var place in Places)
        {
            if (!place.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            var wanted = Segments(place.Path);
            if (wanted.Length != sent.Length || !wanted.Zip(sent).All(Fits)) continue;
            var farm = sent.Zip(wanted).FirstOrDefault(p => p.Second == Farm).First;
            return place.Words.Replace(Farm, DemoUsers.KnownFarms.Contains(farm) ? farm : "a farm");
        }
        return Unknown;
    }

    /// <summary>The parts of a path, ignoring its slashes.</summary>
    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A placeholder fits anything; a word fits only itself.</summary>
    private static bool Fits((string Wanted, string Sent) part) =>
        part.Wanted is AnyId or Farm || part.Wanted.Equals(part.Sent, StringComparison.OrdinalIgnoreCase);
}
