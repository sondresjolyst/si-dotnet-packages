using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>The signing key that the test hosts give the sign-up, and the key of the hashes derived from it.</summary>
internal static class MailKeys
{
    public const string SigningKey = "mail-hash-test-signing-key-0123456789abcdef";

    public static readonly byte[] Key = SignUpMailAddress.DeriveKey(SigningKey);
}

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
        var key = MailKeys.Key;
        Assert.Equal(SignUpMailAddress.MailboxHash(key, "NAME@GMAIL.COM"), SignUpMailAddress.MailboxHash(key, "N.A.M.E+X@GOOGLEMAIL.COM."));
        Assert.NotEqual(SignUpMailAddress.MailboxHash(key, "NAME@EXAMPLE.COM"), SignUpMailAddress.MailboxHash(key, "NAME@EXAMPLE.ORG"));
        Assert.NotEqual(SignUpMailAddress.Hash(key, "NAME@GMAIL.COM"), SignUpMailAddress.Hash(key, "N.A.M.E@GMAIL.COM"));
    }

    [Fact]
    public void Every_hash_is_an_HMAC_SHA256_under_the_derived_key_and_not_the_plain_hash()
    {
        var key = MailKeys.Key;
        var address = "NAME@EXAMPLE.COM";

        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(address))), SignUpMailAddress.Hash(key, address));
        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("network:203.0.113.0"))), SignUpMailAddress.NetworkHash(key, "203.0.113.0"));
        Assert.Equal(SignUpMailAddress.Hash(key, address), SignUpMailAddress.MailboxHash(key, address));
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address))), SignUpMailAddress.Hash(key, address));
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("network:203.0.113.0"))), SignUpMailAddress.NetworkHash(key, "203.0.113.0"));
    }

    [Fact]
    public void Another_signing_key_gives_other_hashes_and_the_derived_key_is_not_the_signing_key()
    {
        var other = SignUpMailAddress.DeriveKey("another-signing-key-0123456789abcdef-01");

        Assert.NotEqual(SignUpMailAddress.Hash(MailKeys.Key, "NAME@EXAMPLE.COM"), SignUpMailAddress.Hash(other, "NAME@EXAMPLE.COM"));
        Assert.NotEqual(SignUpMailAddress.NetworkHash(MailKeys.Key, "203.0.113.0"), SignUpMailAddress.NetworkHash(other, "203.0.113.0"));
        Assert.Equal(SignUpMailAddress.DeriveKey(MailKeys.SigningKey), MailKeys.Key);
        Assert.NotEqual(Encoding.UTF8.GetBytes(MailKeys.SigningKey), MailKeys.Key);
    }
}
