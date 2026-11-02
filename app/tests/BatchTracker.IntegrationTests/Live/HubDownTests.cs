using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

public class HubDownTests(Stack stack) : StackTest(stack)
{
    [Fact]
    public async Task REQ_BT_028_Hub_down_does_not_fail_a_request()
    {
        var olivia = As("olivia");

        var row = await olivia.CreateSettled();
        var moved = await olivia.Advance(row.Batch, "Dose1", row.ETag);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    }
}
