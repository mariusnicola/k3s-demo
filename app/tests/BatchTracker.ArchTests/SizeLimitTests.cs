using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BatchTracker.ArchTests;

/// <summary>REQ-BT-057 for C#: a file or method over its limit in docs/architecture/code-rules.md fails the build.</summary>
public class SizeLimitTests
{
    private const int MethodLimit = 30;
    private const int SourceLimit = 200;
    private const int TestFileLimit = 300;
    private const int FolderLimit = 15;

    private static readonly string[] Scanned = ["app", "deploy", "tests"];
    private static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "TestResults", "node_modules", ".release"
    };

    /// <summary>The folder holding app, found from where the tests run.</summary>
    private static string Root()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "app", "src"))) folder = folder.Parent;
        return folder?.FullName ?? throw new DirectoryNotFoundException("No app folder above the test run.");
    }

    /// <summary>Every C# file under the scanned folders, as paths from the root.</summary>
    private static List<string> Files(string root) => Scanned
        .SelectMany(s => Directory.EnumerateFiles(Path.Combine(root, s), "*.cs", SearchOption.AllDirectories))
        .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
        .Where(f => !f.Split('/').Any(Generated.Contains))
        .Order(StringComparer.Ordinal)
        .ToList();

    private static bool IsTestFile(string path) => path.Split('/').Contains("tests");

    /// <summary>First line a function's own text starts on: attributes sit above the signature, so they do not count.</summary>
    private static SyntaxToken FirstToken(SyntaxNode node) =>
        node is MemberDeclarationSyntax { AttributeLists.Count: > 0 } member
            ? member.AttributeLists.Last().GetLastToken().GetNextToken()
            : node.GetFirstToken();

    /// <summary>Everything the rules call a method, nested ones included, each counted on its own.</summary>
    private static bool IsFunction(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax
            or PropertyDeclarationSyntax { ExpressionBody: not null } or IndexerDeclarationSyntax { ExpressionBody: not null };

    /// <summary>The files over the limit of their type, longest first.</summary>
    private static List<(string File, int Lines, int Limit)> LongFiles(IEnumerable<(string File, int Lines)> files) => files
        .Select(f => (f.File, f.Lines, Limit: IsTestFile(f.File) ? TestFileLimit : SourceLimit))
        .Where(f => f.Lines > f.Limit)
        .OrderByDescending(f => f.Lines)
        .ToList();

    /// <summary>Top-level statements are the body of the entry point the compiler writes, so they count as one function.</summary>
    private static IEnumerable<(int First, int Last)> TopLevelBody(SyntaxTree tree)
    {
        var statements = tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (statements.Count == 0) return [];
        return [(tree.GetLineSpan(statements[0].Span).StartLinePosition.Line, tree.GetLineSpan(statements[^1].Span).EndLinePosition.Line)];
    }

    /// <summary>Each function over the limit in one source text: first line and length.</summary>
    private static List<(int Line, int Length)> LongFunctions(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot().DescendantNodes().Where(IsFunction)
            .Select(n => (
                First: tree.GetLineSpan(FirstToken(n).Span).StartLinePosition.Line,
                Last: tree.GetLineSpan(n.Span).EndLinePosition.Line))
            .Concat(TopLevelBody(tree))
            .Select(s => (Line: s.First + 1, Length: s.Last - s.First + 1))
            .Where(s => s.Length > MethodLimit)
            .ToList();
    }

    /// <summary>Each folder past the limit, counting only the files directly inside it.</summary>
    private static List<(string Folder, int Files)> FullFolders(IEnumerable<string> files) => files
        .GroupBy(f => f[..f.LastIndexOf('/')])
        .Select(g => (Folder: g.Key, Files: g.Count()))
        .Where(g => g.Files > FolderLimit)
        .ToList();

    /// <summary>This many paths directly inside one folder.</summary>
    private static IEnumerable<string> FilesIn(string folder, int count) =>
        Enumerable.Range(0, count).Select(i => $"{folder}/F{i}.cs");

    /// <summary>A method of this many lines, signature and closing line included.</summary>
    private static string Method(int lines, string name = "M", string prefix = "") =>
        $"{prefix}void {name}()\n{{\n" + string.Concat(Enumerable.Repeat("    F();\n", lines - 3)) + "}\n";

    /// <summary>A member with an expression body of this many lines, signature included.</summary>
    private static string ArrowMember(string signature, int lines = 31) =>
        $"{signature} =>\n    1" + string.Concat(Enumerable.Repeat("\n    + 1", lines - 2)) + ";\n";

    private static string Class(string body) => $"class C\n{{\n{body}}}\n";

    /// <summary>A Program file: a using above this many statement lines, and a type below them that does not count.</summary>
    private static string TopLevel(int lines) =>
        "using System;\n" + string.Concat(Enumerable.Repeat("F();\n", lines)) + Class("int a;\n");

    [Fact]
    public void REQ_BT_057_A_method_of_31_lines_is_over()
    {
        Assert.Equal(31, Assert.Single(LongFunctions(Class(Method(31)))).Length);
    }

    [Fact]
    public void REQ_BT_057_A_method_of_30_lines_is_not()
    {
        Assert.Empty(LongFunctions(Class(Method(30))));
    }

    [Fact]
    public void REQ_BT_057_Attributes_above_the_signature_do_not_count()
    {
        Assert.Empty(LongFunctions(Class(Method(30, prefix: "[Fact]\n[Trait(\"a\", \"b\")]\n"))));
    }

    [Fact]
    public void REQ_BT_057_A_nested_function_counts_alone_and_inside_its_method()
    {
        var nested = Method(31, "Inner");
        var outer = $"void Outer()\n{{\n{nested}}}\n";

        var found = LongFunctions(Class(outer));

        Assert.Equal(new[] { 31, 34 }.Order(), found.Select(f => f.Length).Order());
    }

    [Fact]
    public void REQ_BT_057_A_long_lambda_is_over()
    {
        var lambda = "System.Action a = () =>\n{\n" + string.Concat(Enumerable.Repeat("    F();\n", 30)) + "};\n";

        Assert.Single(LongFunctions(Class(lambda)));
    }

    [Fact]
    public void REQ_BT_057_Top_level_statements_of_31_lines_are_one_function_over()
    {
        var found = Assert.Single(LongFunctions(TopLevel(31)));

        Assert.Equal((2, 31), found);
    }

    [Fact]
    public void REQ_BT_057_Top_level_statements_of_30_lines_are_not()
    {
        Assert.Empty(LongFunctions(TopLevel(30)));
    }

    [Fact]
    public void REQ_BT_057_A_blank_line_between_top_level_statements_counts()
    {
        Assert.Single(LongFunctions(TopLevel(30).Replace("F();\nclass", "F();\n\nG();\nclass")));
    }

    [Fact]
    public void REQ_BT_057_A_long_arrow_property_is_over()
    {
        Assert.Equal(31, Assert.Single(LongFunctions(Class(ArrowMember("int P")))).Length);
    }

    [Fact]
    public void REQ_BT_057_A_long_arrow_indexer_is_over()
    {
        Assert.Equal(31, Assert.Single(LongFunctions(Class(ArrowMember("int this[int i]")))).Length);
    }

    [Fact]
    public void REQ_BT_057_An_arrow_property_of_30_lines_is_not()
    {
        Assert.Empty(LongFunctions(Class(ArrowMember("int P", 30))));
    }

    [Fact]
    public void REQ_BT_057_A_long_property_accessor_is_over()
    {
        var property = "int P\n{\nget\n{\n" + string.Concat(Enumerable.Repeat("    F();\n", 30)) + "}\n}\n";

        Assert.Single(LongFunctions(Class(property)));
    }

    [Theory]
    [InlineData("app/src/A.cs", 200, false)]
    [InlineData("app/src/A.cs", 201, true)]
    [InlineData("deploy/monitor/A.cs", 201, true)]
    [InlineData("app/tests/A.cs", 300, false)]
    [InlineData("app/tests/A.cs", 301, true)]
    [InlineData("tests/monitor/A.cs", 301, true)]
    public void REQ_BT_057_A_file_is_held_to_the_limit_of_its_type(string file, int lines, bool over)
    {
        Assert.Equal(over, LongFiles([(file, lines)]).Count == 1);
    }

    [Fact]
    public void REQ_BT_057_A_folder_of_16_files_is_over()
    {
        Assert.Equal(("a/b", 16), Assert.Single(FullFolders(FilesIn("a/b", 16))));
    }

    [Fact]
    public void REQ_BT_057_A_folder_of_15_files_is_not()
    {
        Assert.Empty(FullFolders(FilesIn("a/b", 15)));
    }

    [Fact]
    public void REQ_BT_057_Files_in_a_subfolder_do_not_count_for_its_parent()
    {
        Assert.Empty(FullFolders(FilesIn("a", 15).Concat(FilesIn("a/b", 15))));
    }

    [Fact]
    public void REQ_BT_057_The_scan_sees_all_three_folders_and_no_build_output()
    {
        var files = Files(Root());

        Assert.Contains(files, f => f.StartsWith("app/src/"));
        Assert.Contains(files, f => f.StartsWith("deploy/monitor/"));
        Assert.Contains(files, f => f.StartsWith("tests/monitor/"));
        Assert.DoesNotContain(files, f => f.Contains("/obj/") || f.Contains("/bin/") || f.Contains(".release"));
    }

    [Fact]
    public void REQ_BT_057_No_CSharp_file_is_over_its_limit()
    {
        var root = Root();

        var over = LongFiles(Files(root).Select(f => (f, File.ReadAllLines(Path.Combine(root, f)).Length)))
            .Select(f => $"{f.File}: {f.Lines} lines, limit {f.Limit}")
            .ToList();

        Assert.True(over.Count == 0, $"{over.Count} files over\n{string.Join("\n", over)}");
    }

    [Fact]
    public void REQ_BT_057_No_CSharp_method_is_over_its_limit()
    {
        var root = Root();

        var over = Files(root)
            .SelectMany(f => LongFunctions(File.ReadAllText(Path.Combine(root, f))).Select(m => (File: f, m.Line, m.Length)))
            .OrderByDescending(m => m.Length)
            .Select(m => $"{m.File}:{m.Line}: {m.Length} lines, limit {MethodLimit}")
            .ToList();

        Assert.True(over.Count == 0, $"{over.Count} methods over\n{string.Join("\n", over)}");
    }

    [Fact]
    public void REQ_BT_057_No_CSharp_folder_is_over_its_limit()
    {
        var over = FullFolders(Files(Root()))
            .OrderByDescending(f => f.Files)
            .Select(f => $"{f.Folder}: {f.Files} files, limit {FolderLimit}")
            .ToList();

        Assert.True(over.Count == 0, $"{over.Count} folders over\n{string.Join("\n", over)}");
    }
}
