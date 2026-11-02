using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

public class DeployStepsTests
{
    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private static string Setup => File.ReadAllText(Path.Combine(Root(), "deploy", "setup.ps1"));

    private static string[] Declared()
    {
        var list = Regex.Match(Setup, @"\$DeploySteps = @\(([^)]*)\)");
        Assert.True(list.Success, "setup.ps1 declares no $DeploySteps");
        return Regex.Matches(list.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).ToArray();
    }

    private static string[] Set() =>
        Regex.Matches(Setup, @"Set-OperationStep \$operation '([^']+)'").Select(m => m.Groups[1].Value).ToArray();

    [Fact]
    public void REQ_BT_054_setup_sets_exactly_the_declared_steps_in_order()
    {
        Assert.Equal(Declared(), Set());
    }

    [Fact]
    public void REQ_BT_054_the_deploy_start_line_carries_the_list()
    {
        Assert.Matches(@"Start-Operation ""Deploy [^\n]*'deploy' \$steps", Setup);
    }
}
