using System.Text.Json;
using Xunit;

public class OperationStepsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 6, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Three = ["one", "two", "three"];

    private static Operations New() => new(new SocketHub(Web));

    private static Operation One(Operations operations, string id = "a") =>
        JsonSerializer.SerializeToElement(operations.Frame(), Web).GetProperty("operations").Deserialize<Operation[]>(Web)!.Single(o => o.Id == id);

    private static string[] States(Operation operation) => operation.Steps.Select(s => s.State).ToArray();

    /// <summary>A finished run with one, two and three taking 10, 20 and 30 s.</summary>
    private static void GoodRun(Operations operations, string id = "old", string work = "deploy")
    {
        operations.Start(id, "Deploy", work, Start, Three);
        operations.Step(id, "one", Start);
        operations.Step(id, "two", Start.AddSeconds(10));
        operations.Step(id, "three", Start.AddSeconds(30));
        operations.End(id, true, Start.AddSeconds(60));
    }

    [Fact]
    public void REQ_BT_054_the_whole_list_shows_pending_from_the_start()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start, Three);

        var operation = One(operations);

        Assert.Equal(Three, operation.Steps.Select(s => s.Name));
        Assert.Equal(["pending", "pending", "pending"], States(operation));
        Assert.All(operation.Steps, s => Assert.Null(s.StartedAt));
    }

    [Fact]
    public void REQ_BT_054_a_step_ticks_the_one_before_it()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start, Three);
        operations.Step("a", "one", Start.AddSeconds(1));
        operations.Step("a", "two", Start.AddSeconds(5));

        var operation = One(operations);

        Assert.Equal(["done", "running", "pending"], States(operation));
        Assert.Equal(Start.AddSeconds(1), operation.Steps[0].StartedAt);
        Assert.Equal(Start.AddSeconds(5), operation.Steps[0].EndedAt);
        Assert.Equal(Start.AddSeconds(5), operation.Steps[1].StartedAt);
        Assert.Null(operation.Steps[1].EndedAt);
        Assert.Equal("two", operation.Step);
    }

    [Fact]
    public void REQ_BT_054_a_good_end_ticks_the_last_step()
    {
        var operations = New();
        GoodRun(operations);

        Assert.Equal(["done", "done", "done"], States(One(operations, "old")));
    }

    [Fact]
    public void REQ_BT_054_a_failed_step_stays_marked_and_later_steps_stay_unticked()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start, Three);
        operations.Step("a", "one", Start);
        operations.Step("a", "two", Start.AddSeconds(1));
        operations.End("a", false, Start.AddSeconds(2));
        operations.Tick(Start.AddDays(1));

        var operation = One(operations);

        Assert.Equal(["done", "failed", "pending"], States(operation));
        Assert.Equal(Start.AddSeconds(2), operation.Steps[1].EndedAt);
    }

    [Fact]
    public void REQ_BT_054_a_step_outside_the_list_changes_no_tick_but_shows_as_the_step()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start, Three);
        operations.Step("a", "one", Start);
        operations.Step("a", "surprise", Start.AddSeconds(1));

        var operation = One(operations);

        Assert.Equal(["running", "pending", "pending"], States(operation));
        Assert.Equal("surprise", operation.Step);
    }

    [Fact]
    public void REQ_BT_054_a_start_with_no_list_keeps_todays_behaviour()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start);
        operations.Step("a", "one", Start);

        var operation = One(operations);

        Assert.Empty(operation.Steps);
        Assert.Null(operation.EtaSeconds);
        Assert.Equal("one", operation.Step);
    }

    [Fact]
    public void REQ_BT_054_no_earlier_good_run_means_no_eta()
    {
        var operations = New();
        operations.Start("a", "Deploy", "deploy", Start, Three);

        Assert.Null(One(operations).EtaSeconds);
    }

    [Fact]
    public void REQ_BT_054_eta_sums_the_last_good_run_for_what_has_not_finished()
    {
        var operations = New();
        GoodRun(operations);

        operations.Start("a", "Deploy", "deploy", Start.AddHours(1), Three);
        Assert.Equal(60, One(operations).EtaSeconds);

        operations.Step("a", "one", Start.AddHours(1));
        Assert.Equal(60, One(operations).EtaSeconds);

        operations.Step("a", "two", Start.AddHours(1).AddSeconds(5));
        Assert.Equal(50, One(operations).EtaSeconds);

        operations.Step("a", "three", Start.AddHours(1).AddSeconds(9));
        Assert.Equal(30, One(operations).EtaSeconds);
    }

    [Fact]
    public void REQ_BT_054_only_the_last_good_run_counts_and_a_failed_run_never_does()
    {
        var operations = New();
        GoodRun(operations);
        operations.Start("second", "Deploy", "deploy", Start, Three);
        operations.Step("second", "one", Start);
        operations.Step("second", "two", Start.AddSeconds(1));
        operations.Step("second", "three", Start.AddSeconds(2));
        operations.End("second", true, Start.AddSeconds(3));
        operations.Start("bad", "Deploy", "deploy", Start, Three);
        operations.Step("bad", "one", Start);
        operations.Step("bad", "two", Start.AddSeconds(500));
        operations.End("bad", false, Start.AddSeconds(501));

        operations.Start("a", "Deploy", "deploy", Start, Three);

        Assert.Equal(3, One(operations).EtaSeconds);
    }

    [Fact]
    public void REQ_BT_054_another_work_has_its_own_eta()
    {
        var operations = New();
        GoodRun(operations);

        operations.Start("a", "Move", "move", Start, Three);

        Assert.Null(One(operations).EtaSeconds);
    }

    [Fact]
    public void REQ_BT_054_a_new_step_with_no_history_adds_nothing_to_the_eta()
    {
        var operations = New();
        GoodRun(operations);

        operations.Start("a", "Deploy", "deploy", Start, ["one", "brand new"]);

        Assert.Equal(10, One(operations).EtaSeconds);
    }

    [Fact]
    public void REQ_BT_054_an_ended_run_has_no_eta()
    {
        var operations = New();
        GoodRun(operations);
        operations.Start("a", "Deploy", "deploy", Start, Three);
        operations.End("a", false, Start);

        Assert.Null(One(operations).EtaSeconds);
    }
}
