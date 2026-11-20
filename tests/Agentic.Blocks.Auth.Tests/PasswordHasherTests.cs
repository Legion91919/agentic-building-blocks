using Agentic.Blocks.Auth;
using Xunit;

namespace Agentic.Blocks.Auth.Tests;

public class PasswordHasherTests
{
    private static Pbkdf2PasswordHasher Hasher(int iterations = 1_000) => new(new PasswordHashingOptions { Iterations = iterations });

    [Fact]
    public void Default_parameters_are_600k_sha256_with_16_byte_salt()
    {
        var options = new PasswordHashingOptions();
        Assert.Equal(600_000, options.Iterations);
        var hash = new Pbkdf2PasswordHasher(options).Hash("correct horse battery");
        var parts = hash.Split('$');
        Assert.Equal("v1", parts[0]);
        Assert.Equal("pbkdf2-sha256", parts[1]);
        Assert.Equal("600000", parts[2]);
        Assert.Equal(16, Convert.FromBase64String(parts[3]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[4]).Length);
    }

    [Fact]
    public void Correct_password_verifies()
    {
        var hasher = Hasher();
        Assert.Equal(PasswordCheck.Success, hasher.Verify(hasher.Hash("s3cret-pass-phrase"), "s3cret-pass-phrase"));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var hasher = Hasher();
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(hasher.Hash("s3cret-pass-phrase"), "s3cret-pass-phrasE"));
    }

    [Fact]
    public void Same_password_gets_different_salt()
    {
        var hasher = Hasher();
        Assert.False(hasher.Hash("same password here") == hasher.Hash("same password here"));
    }

    [Fact]
    public void Old_iteration_count_verifies_and_asks_for_rehash()
    {
        var old = Hasher(1_000).Hash("upgrade me please");
        Assert.Equal(PasswordCheck.SuccessRehashNeeded, Hasher(2_000).Verify(old, "upgrade me please"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("v0$pbkdf2-sha256$1000$AAAA$AAAA")]
    [InlineData("v1$pbkdf2-sha1$1000$AAAA$AAAA")]
    [InlineData("v1$pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("v1$pbkdf2-sha256$1000$***$AAAA")]
    [InlineData("v1$pbkdf2-sha256$0$AAAA$AAAA")]
    public void Malformed_hash_fails_safely(string stored)
    {
        Assert.Equal(PasswordCheck.Failed, Hasher().Verify(stored, "anything"));
    }
}
