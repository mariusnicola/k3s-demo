using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.UnitTests;

public class CorrelationTests
{
    [Theory]
    [InlineData("abc-123")]
    [InlineData("A")]
    public void REQ_BT_052_Letters_digits_and_dashes_are_valid(string id) => Assert.True(Correlation.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a_b")]
    [InlineData("abc\n")]
    [InlineData("a<script>")]
    public void REQ_BT_052_Anything_else_is_not(string? id) => Assert.False(Correlation.IsValid(id));

    [Fact]
    public void REQ_BT_052_Length_stops_at_64()
    {
        Assert.True(Correlation.IsValid(new string('a', 64)));
        Assert.False(Correlation.IsValid(new string('a', 65)));
    }

    [Fact]
    public void REQ_BT_052_A_generated_id_is_valid_and_new_each_time()
    {
        var first = Correlation.New();

        Assert.True(Correlation.IsValid(first));
        Assert.NotEqual(first, Correlation.New());
    }

    [Fact]
    public void REQ_BT_052_Nothing_is_current_outside_an_action()
    {
        Assert.Null(Correlation.Current);
    }

    [Fact]
    public void REQ_BT_052_An_action_holds_its_id_and_gives_it_back()
    {
        using (Correlation.Use("outer"))
        {
            using (Correlation.Use("inner")) Assert.Equal("inner", Correlation.Current);
            Assert.Equal("outer", Correlation.Current);
        }

        Assert.Null(Correlation.Current);
    }

    [Fact]
    public async Task REQ_BT_052_Work_started_inside_an_action_keeps_its_id()
    {
        using var action = Correlation.Use("carried");

        Assert.Equal("carried", await Task.Run(async () =>
        {
            await Task.Yield();
            return Correlation.Current;
        }));
    }

    [Fact]
    public void REQ_BT_052_A_new_pending_event_takes_the_id_of_its_action()
    {
        var stamp = new Stamp("ada", "trace", DateTimeOffset.UnixEpoch, "linked");

        var batch = BatchMachine.Create("b", "farm-a", "Vaccine", stamp);

        Assert.Equal("linked", batch.Pending!.CorrelationId);
    }
}
