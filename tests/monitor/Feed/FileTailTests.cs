using System.Text.Json;
using Xunit;

public class FileTailTests
{
    private const string OldDeploy = "Deploy the cluster and the app";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Start(string id, string name, string? work = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["time"] = "2026-10-31T04:00:00+00:00", ["name"] = name, ["id"] = id, ["type"] = "start", ["work"] = work }
            .Where(p => p.Value is not null).ToDictionary());

    private static string End(string id, bool ok) =>
        JsonSerializer.Serialize(new { time = "2026-10-31T04:05:00+00:00", id, type = "end", ok });

    /// <summary>Runs the real tail over a real file, as a monitor start replays it from offset 0.</summary>
    private static async Task<Operation[]> Replay(string last, params string[] lines)
    {
        var folder = Directory.CreateTempSubdirectory("file-tail-").FullName;
        var path = Path.Combine(folder, "changes.jsonl");
        File.WriteAllLines(path, lines);
        var hub = new SocketHub(Web);
        var operations = new Operations(hub);
        var loops = new Loops(_ => { }, TimeSpan.FromMilliseconds(1), _ => { });
        using var stop = new CancellationTokenSource();
        var tail = new FileTail(path, new ChangeFeed(hub), operations, loops).Run(stop.Token);
        for (var i = 0; i < 100 && !States(operations).Any(o => o.Id == last); i++) await Task.Delay(20);
        await stop.CancelAsync();
        await tail;
        Directory.Delete(folder, true);
        return States(operations);
    }

    private static Operation[] States(Operations operations) =>
        JsonSerializer.SerializeToElement(operations.Frame(), Web).GetProperty("operations").Deserialize<Operation[]>(Web)!;

    [Fact]
    public async Task REQ_BT_054_the_step_list_on_a_start_line_reaches_the_frame()
    {
        var start = """{"time":"2026-10-31T04:00:00+00:00","name":"Deploy","id":"a","type":"start","work":"deploy","steps":["one","two"]}""";
        var step = """{"time":"2026-10-31T04:00:01+00:00","id":"a","type":"step","step":"one"}""";

        var states = await Replay("a", start, step);

        Assert.Equal(["one", "two"], states[0].Steps.Select(s => s.Name));
        Assert.Equal(["running", "pending"], states[0].Steps.Select(s => s.State));
    }

    [Fact]
    public async Task REQ_BT_054_replay_gives_the_eta_from_a_good_run_in_the_log()
    {
        var lines = new[]
        {
            """{"time":"2026-10-31T04:00:00+00:00","name":"Deploy","id":"a","type":"start","work":"deploy","steps":["one"]}""",
            """{"time":"2026-10-31T04:00:00+00:00","id":"a","type":"step","step":"one"}""",
            """{"time":"2026-10-31T04:02:00+00:00","id":"a","type":"end","ok":true}""",
            """{"time":"2026-10-31T05:00:00+00:00","name":"Deploy","id":"b","type":"start","work":"deploy","steps":["one"]}"""
        };

        var states = await Replay("b", lines);

        Assert.Equal(120, states.Single(o => o.Id == "b").EtaSeconds);
    }

    [Fact]
    public async Task REQ_BT_043_a_later_deploy_clears_failed_runs_from_before_work_was_written()
    {
        var states = await Replay("c",
            Start("a", OldDeploy), End("a", false),
            Start("b", OldDeploy), End("b", false),
            Start("c", "Deploy 68d0d447ad94", "deploy"), End("c", true));

        Assert.Equal("c", Assert.Single(states).Id);
        Assert.Equal(Operations.Done, states[0].State);
    }

    [Fact]
    public async Task REQ_BT_043_an_old_failed_deploy_stays_until_a_deploy_runs()
    {
        var states = await Replay("a", Start("a", OldDeploy), End("a", false));

        Assert.Equal(Operations.Failed, Assert.Single(states).State);
    }

    [Fact]
    public async Task REQ_BT_043_a_start_with_no_work_and_another_name_keeps_its_name_as_work()
    {
        var states = await Replay("b",
            Start("a", "Move workload"), End("a", false),
            Start("b", "Deploy 68d0d447ad94", "deploy"));

        Assert.Contains(states, o => o.Id == "a" && o.State == Operations.Failed);
    }
}
