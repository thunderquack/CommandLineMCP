using CommandLineMcp.Server;
using Moq;
using System.Security.Claims;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class CommandToolsAuthorizationTests
{
    [TestMethod]
    public async Task SubmitJobAcceptsOwnedEnabledClientAndRejectsForeignOrDisabledClient()
    {
        var userId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var foreignClientId = Guid.NewGuid();
        var access = new Mock<IUserAccessValidator>(MockBehavior.Strict);
        access.Setup(x => x.IsOAuthAllowedAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var clients = new Mock<IClientRepository>(MockBehavior.Strict);
        clients.Setup(x => x.FindByOwnerAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(
            (IReadOnlyList<OwnedClient>)[
                new(clientId, "enabled", true, DateTime.UtcNow, DateTime.UtcNow),
                new(foreignClientId, "disabled", false, DateTime.UtcNow, DateTime.UtcNow)]);
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        var jobId = Guid.NewGuid();
        jobs.Setup(x => x.CreateAsync(clientId, "Get-Date", null, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnedJob(jobId, clientId, "Get-Date", null, 30, JobStatus.Pending,
                null, null, null, DateTime.UtcNow, DateTime.UtcNow));

        var principal = Principal(userId);
        var accepted = await CommandTools.SubmitJob(principal, clients.Object, jobs.Object, access.Object,
            clientId, "Get-Date", null, 30, CancellationToken.None);
        Assert.IsTrue(accepted.Success);
        Assert.AreEqual(jobId, accepted.JobId);

        var foreign = await CommandTools.SubmitJob(principal, clients.Object, jobs.Object, access.Object,
            Guid.NewGuid(), "Get-Date", null, 30, CancellationToken.None);
        Assert.IsFalse(foreign.Success);
        Assert.AreEqual("client_not_found", foreign.Error);

        var disabled = await CommandTools.SubmitJob(principal, clients.Object, jobs.Object, access.Object,
            foreignClientId, "Get-Date", null, 30, CancellationToken.None);
        Assert.IsFalse(disabled.Success);
        Assert.AreEqual("client_not_found", disabled.Error);
        jobs.Verify(x => x.CreateAsync(clientId, "Get-Date", null, 30, It.IsAny<CancellationToken>()), Times.Once);
        access.VerifyAll();
        clients.Verify(x => x.FindByOwnerAsync(userId, It.IsAny<CancellationToken>()), Times.Exactly(3));
        clients.VerifyNoOtherCalls();
        jobs.VerifyAll();
    }

    [TestMethod]
    public async Task SubmitJobSelectsPublishedShellAndRejectsUnknownShell()
    {
        var userId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var access = new Mock<IUserAccessValidator>(MockBehavior.Strict);
        access.Setup(x => x.IsOAuthAllowedAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var clients = new Mock<IClientRepository>(MockBehavior.Strict);
        clients.Setup(x => x.FindByOwnerAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(
            (IReadOnlyList<OwnedClient>)[new(clientId, "worker", true,
                DateTime.UtcNow, DateTime.UtcNow, ShellsJson:
                "[{\"Id\":\"pwsh\",\"Version\":\"7\"},{\"Id\":\"cmd\",\"Version\":null}]")]);
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        jobs.Setup(x => x.CreateAsync(clientId, "echo test", null, 30, It.IsAny<CancellationToken>(),
            JobKind.Command, "cmd")).ReturnsAsync(new OwnedJob(Guid.NewGuid(), clientId,
                "echo test", null, 30, JobStatus.Pending, null, null, null,
                DateTime.UtcNow, DateTime.UtcNow));

        var principal = Principal(userId);
        var accepted = await CommandTools.SubmitJob(principal, clients.Object, jobs.Object,
            access.Object, clientId, "echo test", null, 30, CancellationToken.None, "cmd");
        Assert.IsTrue(accepted.Success);
        var rejected = await CommandTools.SubmitJob(principal, clients.Object, jobs.Object,
            access.Object, clientId, "echo test", null, 30, CancellationToken.None, "bash");
        Assert.AreEqual("unsupported_shell", rejected.Error);
        var malformed = await CommandTools.SubmitJob(principal, clients.Object, jobs.Object,
            access.Object, clientId, "echo test", null, 30, CancellationToken.None, " cmd ");
        Assert.AreEqual("unsupported_shell", malformed.Error);
        jobs.VerifyAll();
    }

    [TestMethod]
    public async Task GetAndWaitJobExposeOnlyOwnedJobAndFinishedStatus()
    {
        var userId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var access = new Mock<IUserAccessValidator>(MockBehavior.Strict);
        access.Setup(x => x.IsOAuthAllowedAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        var finished = new OwnedJob(jobId, Guid.NewGuid(), "Get-Date", null, 30, JobStatus.Succeeded,
            "ok", "", 0, DateTime.UtcNow, DateTime.UtcNow);
        jobs.Setup(x => x.FindOwnedByIdAsync(userId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OwnedJob?)null);
        jobs.Setup(x => x.FindOwnedByIdAsync(userId, jobId, It.IsAny<CancellationToken>())).ReturnsAsync(finished);

        var principal = Principal(userId);
        var result = await CommandTools.GetJob(principal, jobs.Object, access.Object, jobId, CancellationToken.None);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Succeeded", result.Status);
        Assert.AreEqual("ok", result.Stdout);
        Assert.AreEqual(0, result.ExitCode);

        var missing = await CommandTools.GetJob(principal, jobs.Object, access.Object, Guid.NewGuid(), CancellationToken.None);
        Assert.IsFalse(missing.Success);
        Assert.AreEqual("job_not_found", missing.Error);

        var wait = await CommandTools.WaitJob(principal, jobs.Object, access.Object, jobId, 0, CancellationToken.None);
        Assert.IsTrue(wait.Success);
        Assert.AreEqual("Succeeded", wait.Status);
        var tooLong = await CommandTools.WaitJob(principal, jobs.Object, access.Object, jobId, 26, CancellationToken.None);
        Assert.IsFalse(tooLong.Success);
        Assert.AreEqual("invalid_wait_seconds", tooLong.Error);

        access.Verify(x => x.IsOAuthAllowedAsync(userId, It.IsAny<CancellationToken>()), Times.Exactly(4));
        access.VerifyNoOtherCalls();
        jobs.Verify(x => x.FindOwnedByIdAsync(userId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        jobs.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task DisabledUserIsRejectedBeforeToolRepositoryCalls()
    {
        var userId = Guid.NewGuid();
        var access = new Mock<IUserAccessValidator>(MockBehavior.Strict);
        access.Setup(x => x.IsOAuthAllowedAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var clients = new Mock<IClientRepository>(MockBehavior.Strict);
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        var result = await CommandTools.SubmitJob(Principal(userId), clients.Object, jobs.Object, access.Object,
            Guid.NewGuid(), "Get-Date", null, 5, CancellationToken.None);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("unauthorized", result.Error);
        access.VerifyAll();
        clients.VerifyNoOtherCalls();
        jobs.VerifyNoOtherCalls();
    }

    private static ClaimsPrincipal Principal(Guid userId) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString("D"))], "test"));
}
