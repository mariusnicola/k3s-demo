using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

public sealed class ReachabilityTests
{
    private const string NoCosmos = "AccountEndpoint=https://127.0.0.1:9/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;DisableServerCertificateValidation=True;";
    private const string NoBus = "Endpoint=sb://127.0.0.1:9;SharedAccessKeyName=n;SharedAccessKey=k;UseDevelopmentEmulator=true;";

    [Fact]
    public async Task REQ_BT_027_Cosmos_probe_answers_false_when_cosmos_is_away()
    {
        using var client = CosmosBatchStore.CreateClient(NoCosmos);
        using var store = new CosmosBatchStore(client, Env.Database, Env.Container);

        Assert.False(await store.ReachableAsync(default));
    }

    [Fact]
    public async Task REQ_BT_027_Bus_probe_answers_by_tcp_to_the_port_in_the_connection_string()
    {
        Assert.False(await new TcpBusProbe(NoBus).ReachableAsync(default));
        Assert.True(await new TcpBusProbe(Env.Bus).ReachableAsync(default));
    }
}
