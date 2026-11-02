using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

public partial class CommentRuleTests
{
    [GeneratedRegex(@"^\s{0,4}//\s")]
    private static partial Regex PlainComment();

    [GeneratedRegex(@"^\s{0,4}(\[|public |private |internal |protected |static |async Task|Task|record )")]
    private static partial Regex Declaration();

    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Fact]
    public void A_description_above_a_declaration_is_a_summary()
    {
        var folders = new[] { "deploy/monitor", "deploy/netprobe", "tests/smoke/probe", "tests/monitor" };
        var files = folders.SelectMany(f => Directory.GetFiles(Path.Combine(Root(), f), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"));
        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i + 1 < lines.Length; i++)
            {
                if (PlainComment().IsMatch(lines[i]) && (Declaration().IsMatch(lines[i + 1]) || PlainComment().IsMatch(lines[i + 1])))
                    found.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }
        Assert.Empty(found);
    }
}
