using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class ClientJobEndpointMockTests
{
    [TestMethod]
    public async Task PollAndResultEndpointsUseRepositoriesAndMapOutcomesToHttp()
    {
        const string key = "endpoint-mock-test-key";
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var clients = new Mock<IClientRepository>(MockBehavior.Strict);
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        var database = new Mock<IDatabaseMaintenance>(MockBehavior.Strict);
        var oauthScopes = new Mock<IOAuthScopeRepository>(MockBehavior.Strict);
        clients.Setup(x => x.FindByBearerKeyHashCandidatesAsync(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Client>)[new Client { Id = clientId, BearerKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) }]);
        database.Setup(x => x.MigrateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        oauthScopes.Setup(x => x.EnsureCommandsResourceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        jobs.Setup(x => x.ExpireStaleRunningAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        jobs.Setup(x => x.ExpireStaleRunningAsync(clientId, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        jobs.Setup(x => x.ClaimNextAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = jobId, Command = "Get-Date", WorkingDirectory = null, TimeoutSeconds = 7 });
        jobs.SetupSequence(x => x.SubmitResultAsync(clientId, jobId,
                It.Is<JobResultSubmission>(r => r.Stdout == "ok" && r.Stderr == "warning" && r.ExitCode == 0 && !r.TimedOut),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(JobResultSubmissionOutcome.Accepted)
            .ReturnsAsync(JobResultSubmissionOutcome.Conflict);

        using var temporary = new TemporaryHostDirectory();
        var connectionString = $"Data Source={Path.Combine(temporary.Path, "mock-host.db")}";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            TestAuthHostConfiguration.Apply(builder, temporary.Path, connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClientRepository>();
                services.RemoveAll<IJobRepository>();
                services.RemoveAll<IDatabaseMaintenance>();
                services.RemoveAll<IOAuthScopeRepository>();
                services.AddSingleton(clients.Object);
                services.AddSingleton(jobs.Object);
                services.AddSingleton(database.Object);
                services.AddSingleton(oauthScopes.Object);
            });
        });
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var poll = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.OK, poll.StatusCode);
        var job = await poll.Content.ReadFromJsonAsync<PollResponse>();
        Assert.IsNotNull(job);
        Assert.AreEqual(jobId, job.JobId);
        Assert.IsNull(job.WorkingDirectory);

        var body = new { stdout = "ok", stderr = "warning", exitCode = 0, timedOut = false };
        Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/api/client/jobs/{jobId}/result", body)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await http.PostAsJsonAsync($"/api/client/jobs/{jobId}/result", body)).StatusCode);
        jobs.Verify(x => x.ExpireStaleRunningAsync(clientId, It.IsAny<CancellationToken>()), Times.Once);
        jobs.Verify(x => x.ExpireStaleRunningAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        jobs.Verify(x => x.ClaimNextAsync(clientId, It.IsAny<CancellationToken>()), Times.Once);
        jobs.Verify(x => x.SubmitResultAsync(clientId, jobId, It.IsAny<JobResultSubmission>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        database.VerifyAll();
        clients.VerifyAll();
        jobs.VerifyAll();
    }

    private sealed record PollResponse(Guid JobId, string Command, string? WorkingDirectory, int TimeoutSeconds);

    private sealed class TemporaryHostDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CommandLineMcp.Tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
