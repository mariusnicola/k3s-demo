using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.UnitTests;

/// <summary>The forwarded header names the client only when it came from a proxy we trust.</summary>
public class ClientAddressTests
{
    private const string Header = "X-Forwarded-For";

    /// <summary>A request from this peer, on a service that trusts these networks.</summary>
    private static DefaultHttpContext From(string peer, string? trusted, string? header = "203.0.113.9")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(TrustedProxies.From(trusted)).BuildServiceProvider()
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (header is not null) context.Request.Headers[Header] = header;
        return context;
    }

    [Fact]
    public void B_010_A_header_from_a_peer_not_trusted_is_ignored()
    {
        Assert.Equal("198.51.100.50", ClientAddress.Of(From("198.51.100.50", "198.18.0.0/16")));
    }

    [Fact]
    public void B_010_With_no_list_every_header_is_ignored()
    {
        Assert.Equal("198.18.0.7", ClientAddress.Of(From("198.18.0.7", null)));
    }

    [Fact]
    public void B_010_A_trusted_peer_names_the_client_by_the_last_entry()
    {
        Assert.Equal("203.0.113.9", ClientAddress.Of(From("198.18.0.7", "198.18.0.0/16", "1.1.1.1, 203.0.113.9")));
    }

    [Fact]
    public void B_010_A_trusted_peer_with_no_header_is_itself_the_client()
    {
        Assert.Equal("198.18.0.7", ClientAddress.Of(From("198.18.0.7", "198.18.0.0/16", null)));
    }

    [Fact]
    public void B_010_An_ipv4_peer_written_as_ipv6_still_matches_the_list()
    {
        Assert.Equal("203.0.113.9", ClientAddress.Of(From("::ffff:198.18.0.7", "198.18.0.0/16")));
    }

    [Fact]
    public void B_010_A_client_named_by_a_mapped_peer_outside_the_list_reads_as_ipv4()
    {
        Assert.Equal("198.51.100.50", ClientAddress.Of(From("::ffff:198.51.100.50", "198.18.0.0/16")));
    }

    [Fact]
    public void B_010_Several_networks_are_read_from_the_setting()
    {
        var proxies = TrustedProxies.From(" 198.18.0.0/16, 198.19.18.1/32 ");

        Assert.True(proxies.Has(IPAddress.Parse("198.19.18.1")));
        Assert.False(proxies.Has(IPAddress.Parse("198.19.18.2")));
    }

    [Fact]
    public void B_010_A_bad_setting_stops_the_start_instead_of_trusting_nobody_in_silence()
    {
        Assert.Throws<FormatException>(() => TrustedProxies.From("198.18.0.0/99"));
    }
}
