using Moq;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class UserAccessValidatorTests
{
    [TestMethod]
    public async Task AccessIsAllowedOnlyForAnExistingEnabledUser()
    {
        var allowedId = Guid.NewGuid();
        var deniedId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        users.Setup(x => x.FindByIdAsync(allowedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = allowedId, IsOAuthAllowed = true });
        users.Setup(x => x.FindByIdAsync(deniedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = deniedId, IsOAuthAllowed = false });
        users.Setup(x => x.FindByIdAsync(missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var validator = new UserAccessValidator(users.Object);

        Assert.IsTrue(await validator.IsOAuthAllowedAsync(allowedId));
        Assert.IsFalse(await validator.IsOAuthAllowedAsync(deniedId));
        Assert.IsFalse(await validator.IsOAuthAllowedAsync(missingId));
        users.VerifyAll();
    }
}
