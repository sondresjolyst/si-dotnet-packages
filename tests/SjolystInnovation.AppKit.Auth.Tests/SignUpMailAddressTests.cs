using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class SignUpMailAddressTests
{
    [Theory]
    [InlineData("NAME@EXAMPLE.COM", "NAME@EXAMPLE.COM")]
    [InlineData("NAME+ONE@EXAMPLE.COM", "NAME@EXAMPLE.COM")]
    [InlineData("NAME+ONE+TWO@EXAMPLE.COM", "NAME@EXAMPLE.COM")]
    [InlineData("NAME@EXAMPLE.COM.", "NAME@EXAMPLE.COM")]
    [InlineData("NAME@EXAMPLE.COM...", "NAME@EXAMPLE.COM")]
    [InlineData("NA.ME@EXAMPLE.COM", "NA.ME@EXAMPLE.COM")]
    [InlineData("NAME@B\u00dcCHER.EXAMPLE", "NAME@XN--BCHER-KVA.EXAMPLE")]
    [InlineData("NAME@XN--BCHER-KVA.EXAMPLE", "NAME@XN--BCHER-KVA.EXAMPLE")]
    [InlineData("NAME@GOOGLEMAIL.COM", "NAME@GMAIL.COM")]
    [InlineData("N.A.M.E+TAG@GMAIL.COM", "NAME@GMAIL.COM")]
    [InlineData("N.A.M.E@GOOGLEMAIL.COM.", "NAME@GMAIL.COM")]
    [InlineData("N.A.M.E@OUTLOOK.COM", "N.A.M.E@OUTLOOK.COM")]
    [InlineData("NOT AN ADDRESS", "NOT AN ADDRESS")]
    public void The_key_folds_the_spellings_that_reach_one_inbox(string address, string expected) =>
        Assert.Equal(expected, SignUpMailAddress.MailboxKey(address));

    [Fact]
    public void A_domain_that_cannot_be_converted_stays_as_it_is()
    {
        var address = "NAME@" + new string('A', 70) + ".EXAMPLE";

        Assert.Equal(address, SignUpMailAddress.MailboxKey(address));
    }

    [Fact]
    public void Spellings_of_one_inbox_share_a_mailbox_hash_and_the_address_hash_stays_exact()
    {
        Assert.Equal(SignUpMailAddress.MailboxHash("NAME@GMAIL.COM"), SignUpMailAddress.MailboxHash("N.A.M.E+X@GOOGLEMAIL.COM."));
        Assert.NotEqual(SignUpMailAddress.MailboxHash("NAME@EXAMPLE.COM"), SignUpMailAddress.MailboxHash("NAME@EXAMPLE.ORG"));
        Assert.NotEqual(SignUpMailAddress.Hash("NAME@GMAIL.COM"), SignUpMailAddress.Hash("N.A.M.E@GMAIL.COM"));
    }
}
