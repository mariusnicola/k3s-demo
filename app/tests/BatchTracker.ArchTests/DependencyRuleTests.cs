using System.Reflection;
using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Dispatcher;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using BatchTracker.Live;
using BatchTracker.Worker;
using NetArchTest.Rules;

namespace BatchTracker.ArchTests;

public class DependencyRuleTests
{
    private static readonly Assembly Domain = typeof(Batch).Assembly;
    private static readonly Assembly Application = typeof(BatchService).Assembly;
    private static readonly Assembly Infrastructure = typeof(CosmosBatchStore).Assembly;

    private static readonly string[] OuterLayers =
    [
        "BatchTracker.Infrastructure", "BatchTracker.Api", "BatchTracker.Worker",
        "BatchTracker.Dispatcher", "BatchTracker.Live", "BatchTracker.Line"
    ];

    private static readonly string[] CloudClients = ["Microsoft.Azure.Cosmos", "Azure.Messaging.ServiceBus"];

    private static void Assert(TestResult result) =>
        Xunit.Assert.True(result.IsSuccessful, "Violations: " + string.Join(", ", result.FailingTypeNames ?? []));

    [Fact]
    public void REQ_BT_017_Domain_depends_on_no_other_layer_or_library()
    {
        Assert(Types.InAssembly(Domain).Should()
            .NotHaveDependencyOnAny([.. OuterLayers, "BatchTracker.Application", .. CloudClients, "Microsoft.AspNetCore", "Microsoft.Extensions", "k8s"])
            .GetResult());
    }

    [Fact]
    public void REQ_BT_017_Domain_assembly_references_only_the_base_library()
    {
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name!);

        Xunit.Assert.All(references, name => Xunit.Assert.True(
            name.StartsWith("System", StringComparison.Ordinal), $"Domain references {name}"));
    }

    [Fact]
    public void REQ_BT_017_Application_depends_only_on_Domain()
    {
        Assert(Types.InAssembly(Application).Should()
            .NotHaveDependencyOnAny([.. OuterLayers, .. CloudClients, "Microsoft.AspNetCore", "k8s"])
            .GetResult());
    }

    [Fact]
    public void REQ_BT_017_Infrastructure_does_not_reach_into_hosts()
    {
        Assert(Types.InAssembly(Infrastructure).Should()
            .NotHaveDependencyOnAny(
                "BatchTracker.Api", "BatchTracker.Worker", "BatchTracker.Dispatcher", "BatchTracker.Live", "BatchTracker.Line")
            .GetResult());
    }

    [Fact]
    public void REQ_BT_017_Api_never_calls_Cosmos_or_Service_Bus_directly()
    {
        Assert(Types.InAssembly(typeof(ApiMarker).Assembly).Should().NotHaveDependencyOnAny(CloudClients).GetResult());
    }

    [Fact]
    public void REQ_BT_017_Other_hosts_never_call_Cosmos_or_Service_Bus_directly()
    {
        foreach (var host in new[] { typeof(WorkerMarker), typeof(DispatcherMarker), typeof(LiveMarker), typeof(LineMarker) })
            Assert(Types.InAssembly(host.Assembly).Should().NotHaveDependencyOnAny(CloudClients).GetResult());
    }
}
