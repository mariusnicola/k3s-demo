using System.Text.Json;
using Xunit;

public class OperationsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 6, 0, 0, TimeSpan.Zero);

    private static Operation[] States(Operations operations)
    {
        var frame = JsonSerializer.SerializeToElement(operations.Frame(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return frame.GetProperty("operations").Deserialize<Operation[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static Operations New() => new(new SocketHub(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public void REQ_BT_043_silence_past_60_s_reads_no_signal()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start);
        operations.Tick(Start.AddSeconds(60));
        Assert.Equal(Operations.Running, States(operations).Single().State);
        operations.Tick(Start.AddSeconds(61));
        Assert.Equal(Operations.NoSignal, States(operations).Single().State);
    }

    [Fact]
    public void REQ_BT_043_a_heartbeat_brings_a_silent_operation_back()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start);
        operations.Tick(Start.AddSeconds(61));
        operations.Heartbeat("a", Start.AddSeconds(62));
        Assert.Equal(Operations.Running, States(operations).Single().State);
    }

    [Fact]
    public void REQ_BT_043_done_clears_after_15_s_and_failed_stays()
    {
        var operations = New();
        operations.Start("a", "One", "one", Start);
        operations.Start("b", "Two", "two", Start);
        operations.End("a", true, Start);
        operations.End("b", false, Start);
        operations.Tick(Start.AddSeconds(16));
        Assert.Equal("b", States(operations).Single().Id);
        operations.Tick(Start.AddDays(2));
        Assert.Equal(Operations.Failed, States(operations).Single().State);
    }

    [Fact]
    public void REQ_BT_043_the_same_work_clears_a_failure_under_a_new_name()
    {
        var operations = New();
        operations.Start("a", "Deploy abc", "deploy", Start);
        operations.End("a", false, Start);
        operations.Start("b", "Deploy def", "deploy", Start.AddMinutes(1));
        Assert.Equal("b", States(operations).Single().Id);
    }
}
