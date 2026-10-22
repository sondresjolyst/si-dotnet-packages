using Xunit;

namespace SjolystInnovation.AppKit.Email.Tests;

public class EmailAddressTests
{
    [Theory]
    [InlineData("kari@example.no")]
    [InlineData("kari+shop@example.no")]
    [InlineData("kari.nordmann@sub.example.no")]
    [InlineData("  kari@example.no  ")]
    public void An_ordinary_address_is_single(string value) => Assert.True(EmailAddress.IsSingle(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kari")]
    [InlineData("a@example.no,b@example.no")]
    [InlineData("a@example.no;b@example.no")]
    [InlineData("Kari <kari@example.no>")]
    [InlineData("<kari@example.no>")]
    [InlineData("\"kari\"@example.no")]
    [InlineData("kari\\@x@example.no")]
    [InlineData("kari@[127.0.0.1]")]
    [InlineData("kari@example.no\r\nBcc: x@example.no")]
    [InlineData("kari@example.no\n")]
    [InlineData("kari@example.no.")]
    [InlineData("kari\0@example.no")]
    [InlineData("a@example.no b@example.no")]
    public void Anything_but_one_plain_address_is_refused(string? value) => Assert.False(EmailAddress.IsSingle(value));
}
