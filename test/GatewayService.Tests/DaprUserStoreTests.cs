using Dapr.Client;
using Moq;
using Xunit;

public class DaprUserStoreTests
{
    private static UserAccount Account(string email = "Demo@Example.com") => new()
    {
        Email = email,
        PasswordHash = "100000.c2FsdA==.aGFzaA==",
        Role = "submitter",
        Name = "Demo User"
    };

    [Fact]
    public async Task FindByEmailAsync_ReturnsStoredAccount()
    {
        var daprMock = new Mock<DaprClient>();
        var account = Account();
        daprMock.Setup(c => c.GetStateAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync(account);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.FindByEmailAsync("Demo@Example.com");

        Assert.Same(account, result);
    }

    [Fact]
    public async Task BuildKey_NormalizesCaseAndWhitespace()
    {
        Assert.Equal("user-demo@example.com", DaprUserStore.BuildKey("  Demo@Example.com  "));
    }

    [Fact]
    public async Task TryRegisterAsync_FreshEmail_Succeeds()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync(((UserAccount?)null, ""));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", "user-demo@example.com", It.IsAny<UserAccount>(), "", null, null, default))
            .ReturnsAsync(true);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryRegisterAsync(Account());

        Assert.True(result);
    }

    [Fact]
    public async Task TryRegisterAsync_EmailAlreadyExists_Fails()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync((Account(), "etag-1"));

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryRegisterAsync(Account());

        Assert.False(result);
        daprMock.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UserAccount>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task TryRegisterAsync_LosingTheRace_Fails()
    {
        // Two concurrent registrations for the same email: both read "unclaimed", only one
        // ETag-conditional write can win. The loser must report failure, not silently
        // overwrite the winner's account.
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync(((UserAccount?)null, ""));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", "user-demo@example.com", It.IsAny<UserAccount>(), "", null, null, default))
            .ReturnsAsync(false);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryRegisterAsync(Account());

        Assert.False(result);
    }

    [Fact]
    public async Task TrySetRoleAsync_ExistingAccount_UpdatesRole()
    {
        var daprMock = new Mock<DaprClient>();
        var account = Account();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync((account, "etag-1"));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", "user-demo@example.com", It.Is<UserAccount>(a => a.Role == "approver"), "etag-1", null, null, default))
            .ReturnsAsync(true);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TrySetRoleAsync("Demo@Example.com", "approver");

        Assert.Equal(SetRoleResult.Updated, result);
    }

    [Fact]
    public async Task TrySetRoleAsync_NoSuchAccount_ReturnsNotFound()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync(((UserAccount?)null, ""));

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TrySetRoleAsync("Demo@Example.com", "approver");

        Assert.Equal(SetRoleResult.NotFound, result);
        daprMock.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UserAccount>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task TrySetRoleAsync_LosingTheRace_ReturnsConflict()
    {
        // Someone else wrote this account (e.g. a concurrent role change) between the read
        // and this write — must not silently clobber whatever they set.
        var daprMock = new Mock<DaprClient>();
        var account = Account();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync((account, "etag-1"));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", "user-demo@example.com", It.IsAny<UserAccount>(), "etag-1", null, null, default))
            .ReturnsAsync(false);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TrySetRoleAsync("Demo@Example.com", "approver");

        Assert.Equal(SetRoleResult.Conflict, result);
    }

    [Fact]
    public async Task TryChangePasswordAsync_CorrectCurrentPassword_Succeeds()
    {
        var account = Account();
        account.PasswordHash = PasswordHasher.Hash("old-password");
        var newHash = PasswordHasher.Hash("new-password");

        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync((account, "etag-1"));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", "user-demo@example.com", It.Is<UserAccount>(a => a.PasswordHash == newHash), "etag-1", null, null, default))
            .ReturnsAsync(true);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryChangePasswordAsync("Demo@Example.com", "old-password", newHash);

        Assert.Equal(ChangePasswordResult.Updated, result);
    }

    [Fact]
    public async Task TryChangePasswordAsync_WrongCurrentPassword_Fails()
    {
        var account = Account();
        account.PasswordHash = PasswordHasher.Hash("old-password");

        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync((account, "etag-1"));

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryChangePasswordAsync("Demo@Example.com", "wrong-password", PasswordHasher.Hash("new-password"));

        Assert.Equal(ChangePasswordResult.InvalidCurrentPassword, result);
        daprMock.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UserAccount>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task TryChangePasswordAsync_NoSuchAccount_ReturnsNotFound()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync(((UserAccount?)null, ""));

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryChangePasswordAsync("Demo@Example.com", "old-password", PasswordHasher.Hash("new-password"));

        Assert.Equal(ChangePasswordResult.NotFound, result);
    }

    [Fact]
    public async Task TryChangePasswordAsync_LosingTheRace_ReturnsConflict()
    {
        var account = Account();
        account.PasswordHash = PasswordHasher.Hash("old-password");

        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<UserAccount?>("statestore", "user-demo@example.com", null, null, default))
            .ReturnsAsync((account, "etag-1"));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", "user-demo@example.com", It.IsAny<UserAccount>(), "etag-1", null, null, default))
            .ReturnsAsync(false);

        var store = new DaprUserStore(daprMock.Object);
        var result = await store.TryChangePasswordAsync("Demo@Example.com", "old-password", PasswordHasher.Hash("new-password"));

        Assert.Equal(ChangePasswordResult.Conflict, result);
    }
}
