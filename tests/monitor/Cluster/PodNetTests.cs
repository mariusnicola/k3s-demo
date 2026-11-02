using System.Text.Json;
using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class PodNetTests
{
    private const string TwoPods = """
        {"kind":"PodList","apiVersion":"v1","items":[
          {"metadata":{"name":"probe-a"},"spec":{"nodeName":"a"},"status":{"phase":"Running","podIP":"198.18.2.1"}},
          {"metadata":{"name":"probe-b"},"spec":{"nodeName":"b"},"status":{"phase":"Running","podIP":"198.18.2.2"}}]}
        """;

    private static async Task<IReadOnlyList<NetCheck>> ReadAgainst(RequestDelegate api)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(api);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var kube = new Kubernetes(new KubernetesClientConfiguration { Host = address });
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var checks = await new PodNet(kube, "app", ["a", "b"], new JsonSerializerOptions()).Read(limit.Token);
        await app.StopAsync();
        return checks;
    }

    private static Task Hang(HttpContext context) => Task.Delay(Timeout.Infinite, context.RequestAborted);

    [Fact]
    public async Task A_pod_list_that_never_answers_is_a_failing_check()
    {
        var checks = await ReadAgainst(Hang);

        Assert.Equal(4, checks.Count);
        Assert.All(checks, c => Assert.NotNull(c.Error));
    }

    [Fact]
    public async Task A_probe_that_never_answers_is_a_failing_check()
    {
        var checks = await ReadAgainst(context => context.Request.Path.Value!.Contains("/proxy/")
            ? Hang(context)
            : context.Response.WriteAsync(TwoPods));

        Assert.Equal(4, checks.Count);
        Assert.All(checks, c => Assert.NotNull(c.Error));
    }
}
