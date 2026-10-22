using System.Net;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed class IpTruncatorTests
{
    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0")]
    [InlineData("10.1.2.3", "10.1.2.0")]
    [InlineData("203.0.113.0", "203.0.113.0")]
    [InlineData("255.255.255.255", "255.255.255.0")]
    public void Truncates_IPv4_to_slash_24(string input, string expected) =>
        Assert.Equal(expected, IpTruncator.Truncate(input));

    [Theory]
    [InlineData("2001:db8:abcd:1234:5678:9abc:def0:1234", "2001:db8:abcd::")]
    [InlineData("2001:db8:abcd::1", "2001:db8:abcd::")]
    [InlineData("2001:db8:abcd::", "2001:db8:abcd::")]
    [InlineData("2a01:4f8:1:2:3:4:5:6", "2a01:4f8:1::")]
    public void Truncates_IPv6_to_slash_48(string input, string expected) =>
        Assert.Equal(expected, IpTruncator.Truncate(input));

    [Theory]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0")]
    [InlineData("::ffff:cb00:714d", "203.0.113.0")]
    [InlineData("::FFFF:10.1.2.3", "10.1.2.0")]
    public void Maps_IPv4_mapped_IPv6_back_to_IPv4_before_truncating(string input, string expected) =>
        Assert.Equal(expected, IpTruncator.Truncate(input));

    [Fact]
    public void Truncates_IPv4_loopback() => Assert.Equal("127.0.0.0", IpTruncator.Truncate("127.0.0.1"));

    [Fact]
    public void Truncates_IPv6_loopback_to_the_unspecified_address() =>
        Assert.Equal("::", IpTruncator.Truncate("::1"));

    [Fact]
    public void Truncates_mapped_IPv4_loopback() =>
        Assert.Equal("127.0.0.0", IpTruncator.Truncate("::ffff:127.0.0.1"));

    [Fact]
    public void Drops_the_IPv6_scope_id() =>
        Assert.Equal("fe80::", IpTruncator.Truncate("fe80::1%eth0"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("999.1.1.1")]
    [InlineData(" 203.0.113.77 ")]
    [InlineData("203.0.113.77:443")]
    [InlineData("203.0.113.77/24")]
    public void Returns_empty_for_input_that_cannot_be_parsed_as_an_IP_address(string? input) =>
        Assert.Equal(string.Empty, IpTruncator.Truncate(input));

    [Fact]
    public void Truncates_a_bracketed_IPv6_address_with_a_port() =>
        Assert.Equal("2001:db8::", IpTruncator.Truncate("[2001:db8::1]:443"));

    [Fact]
    public void Truncates_an_IPAddress()
    {
        Assert.Equal("203.0.113.0", IpTruncator.Truncate(IPAddress.Parse("203.0.113.77")));
        Assert.Equal("203.0.113.0", IpTruncator.Truncate(IPAddress.Parse("::ffff:203.0.113.77")));
        Assert.Equal("2001:db8:abcd::", IpTruncator.Truncate(IPAddress.Parse("2001:db8:abcd:1::9")));
    }

    [Fact]
    public void Returns_empty_for_a_null_IPAddress() => Assert.Equal(string.Empty, IpTruncator.Truncate((IPAddress?)null));
}
