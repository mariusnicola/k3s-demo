using System.Net;
using System.Runtime.CompilerServices;
using Xunit;

public class TestHostTests
{
    private static string Folder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Fact]
    public void No_test_lets_a_port_go_before_its_host_binds()
    {
        var chooser = "Free" + "Port(";

        var found = Directory.GetFiles(Folder(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => File.ReadAllText(f).Contains(chooser))
            .Select(Path.GetFileName);

        Assert.Empty(found);
    }

    [Fact]
    public async Task A_host_gets_its_port_as_it_binds()
    {
        await using var host = TestHost.Create();
        host.MapGet("/", () => "up");
        await host.StartAsync();

        using var http = new HttpClient();
        using var answer = await http.GetAsync(TestHost.Url(host));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    [Fact]
    public async Task Two_hosts_run_at_once_on_different_ports()
    {
        await using var first = TestHost.Create();
        await using var second = TestHost.Create();

        await first.StartAsync();
        await second.StartAsync();

        Assert.NotEqual(TestHost.Url(first), TestHost.Url(second));
    }

    [Fact]
    public async Task A_held_port_is_known_before_start_and_serves()
    {
        var (host, ports, release) = TestHost.CreateHolding(2);
        await using var _ = host;
        host.MapGet("/", (HttpContext context) => context.Connection.LocalPort.ToString());
        await host.StartAsync();

        using var http = new HttpClient();

        Assert.Equal(ports[1].ToString(), await http.GetStringAsync($"http://127.0.0.1:{ports[1]}/"));
        Assert.Equal(ports[0].ToString(), await http.GetStringAsync($"http://127.0.0.1:{ports[0]}/"));
        release();
    }
}
