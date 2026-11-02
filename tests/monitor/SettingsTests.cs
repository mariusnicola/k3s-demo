using Xunit;

public class SettingsTests
{
    private const string Name = "MONITOR_TEST_SETTING";

    public static TheoryData<Func<string, string>> Readers => new()
    {
        name => Settings.Required(name),
        name => Netprobe.Settings.Required(name),
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void TM_2493_missing_value_stops_the_start(Func<string, string> read)
    {
        Environment.SetEnvironmentVariable(Name, null);
        Assert.Contains(Name, Assert.Throws<InvalidOperationException>(() => read(Name)).Message);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void TM_2493_empty_value_stops_the_start(Func<string, string> read)
    {
        Environment.SetEnvironmentVariable(Name, "  ");
        Assert.Contains(Name, Assert.Throws<InvalidOperationException>(() => read(Name)).Message);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void TM_2493_a_value_is_returned(Func<string, string> read)
    {
        Environment.SetEnvironmentVariable(Name, "value");
        Assert.Equal("value", read(Name));
    }
}
