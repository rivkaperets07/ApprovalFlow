using Xunit;

public class PasswordHasherTests
{
    [Fact]
    public void CorrectPassword_Verifies()
    {
        var hash = PasswordHasher.Hash("correct-horse-battery");

        Assert.True(PasswordHasher.Verify("correct-horse-battery", hash));
    }

    [Fact]
    public void WrongPassword_DoesNotVerify()
    {
        var hash = PasswordHasher.Hash("correct-horse-battery");

        Assert.False(PasswordHasher.Verify("wrong-password", hash));
    }

    [Fact]
    public void TwoHashesOfTheSamePassword_AreDifferent()
    {
        // Per-user random salt: identical passwords must not produce identical stored
        // hashes, or a leaked state store would reveal which accounts share a password.
        var hash1 = PasswordHasher.Hash("correct-horse-battery");
        var hash2 = PasswordHasher.Hash("correct-horse-battery");

        Assert.NotEqual(hash1, hash2);
        Assert.True(PasswordHasher.Verify("correct-horse-battery", hash1));
        Assert.True(PasswordHasher.Verify("correct-horse-battery", hash2));
    }

    [Theory]
    [InlineData("not-the-right-shape")]
    [InlineData("100000.onlytwoparts")]
    [InlineData("notanumber.c2FsdA==.aGFzaA==")]
    [InlineData("100000.not-base64!!.aGFzaA==")]
    [InlineData("")]
    public void MalformedEncodedHash_FailsClosedInsteadOfThrowing(string malformed)
    {
        Assert.False(PasswordHasher.Verify("anything", malformed));
    }
}
