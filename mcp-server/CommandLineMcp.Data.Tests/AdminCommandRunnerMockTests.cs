using Moq;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class AdminCommandRunnerMockTests
{
    [TestMethod]
    public async Task InvalidArgumentsReturnCodeTwoWithoutDatabaseAccess()
    {
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        var database = new Mock<IDatabaseMaintenance>(MockBehavior.Strict);
        var stderr = new StringWriter();

        var exitCode = await AdminCommandRunner.RunAsync(["users", "set-role", "1", "2"],
            users.Object, database.Object, TextWriter.Null, stderr);

        Assert.AreEqual(2, exitCode);
        StringAssert.Contains(stderr.ToString(), "Role must be 0 or 1.");
        database.VerifyNoOtherCalls();
        users.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task DatabaseFailureReturnsCodeOneAndRedactedMessage()
    {
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        var database = new Mock<IDatabaseMaintenance>(MockBehavior.Strict);
        database.Setup(x => x.MigrateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("private details"));
        var stderr = new StringWriter();

        var exitCode = await AdminCommandRunner.RunAsync(["users", "show", "123"],
            users.Object, database.Object, TextWriter.Null, stderr);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual("Database operation failed." + Environment.NewLine, stderr.ToString());
        Assert.IsFalse(stderr.ToString().Contains("private details", StringComparison.Ordinal));
        database.Verify(x => x.MigrateAsync(It.IsAny<CancellationToken>()), Times.Once);
        users.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task MissingUserFromRepositoryReturnsCodeThree()
    {
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        var database = new Mock<IDatabaseMaintenance>(MockBehavior.Strict);
        database.Setup(x => x.MigrateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        users.Setup(x => x.SetRoleAsync(It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 123L })), 1,
            It.IsAny<CancellationToken>())).ReturnsAsync(new UserBatchUpdateResult([123]));
        var stderr = new StringWriter();

        var exitCode = await AdminCommandRunner.RunAsync(["users", "set-role", "123", "1"],
            users.Object, database.Object, TextWriter.Null, stderr);

        Assert.AreEqual(3, exitCode);
        StringAssert.Contains(stderr.ToString(), "User(s) not found: 123.");
        users.VerifyAll();
        database.VerifyAll();
    }
}
