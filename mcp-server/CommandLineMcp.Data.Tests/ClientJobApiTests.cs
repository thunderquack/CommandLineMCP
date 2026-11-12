using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class ClientJobApiTests
{
    [TestMethod]
    public async Task ClientPollClaimsOnceAndResultIsIdempotentAndScoped()
    {
        using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        using var factory = fixture.CreateFactory();
        using var http = factory.CreateClient();

        var missingAuth = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingAuth.StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        var claim = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
        var job = await claim.Content.ReadFromJsonAsync<PollResponse>();
        Assert.IsNotNull(job);
        Assert.AreEqual(fixture.JobId, job.JobId);
        Assert.AreEqual("Get-Location", job.Command);

        var blocked = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.NoContent, blocked.StatusCode);

        var resultBody = new { stdout = "C:\\work\n", stderr = "", exitCode = 0, timedOut = false };
        var resultUrl = $"/api/client/jobs/{fixture.JobId}/result";
        Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsJsonAsync(resultUrl, resultBody)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsJsonAsync(resultUrl, resultBody)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync(resultUrl, new { stdout = "different", stderr = "", exitCode = 0, timedOut = false })).StatusCode);

        await using var verify = new CommandLineMcpDbContext(fixture.Options);
        var saved = await verify.Jobs.SingleAsync(j => j.Id == fixture.JobId);
        Assert.AreEqual(JobStatus.Succeeded, saved.Status);
        Assert.AreEqual("C:\\work\n", saved.Stdout);
        Assert.AreEqual(0, saved.ExitCode);
    }

    [TestMethod]
    public async Task UnknownBearerAndForeignJobAreNotAccessible()
    {
        using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        using var factory = fixture.CreateFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync($"/api/client/jobs/{fixture.ForeignJobId}/result", new { stdout = "", stderr = "", exitCode = 0, timedOut = false })).StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentPollsClaimOnlyOneJobForClient()
    {
        using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        await fixture.AddJobsAsync("parallel", 4);
        using var factory = fixture.CreateFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);

        var polls = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 })));

        Assert.AreEqual(1, polls.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.AreEqual(11, polls.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        await using var verify = new CommandLineMcpDbContext(fixture.Options);
        var running = await verify.Jobs.Where(j => j.ClientId == fixture.ClientId && j.Status == JobStatus.Running).ToListAsync();
        Assert.AreEqual(1, running.Count);
        Assert.AreEqual(4, await verify.Jobs.CountAsync(j => j.ClientId == fixture.ClientId && j.Status == JobStatus.Pending));
    }

    [TestMethod]
    public async Task ConcurrentConflictingResultSubmissionsHaveOneWinnerAndRetriesAreIdempotent()
    {
        using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        using var factory = fixture.CreateFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        Assert.AreEqual(HttpStatusCode.OK,
            (await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 })).StatusCode);

        var url = $"/api/client/jobs/{fixture.JobId}/result";
        var candidates = new[]
        {
            new { stdout = "result-a", stderr = "", exitCode = 0, timedOut = false },
            new { stdout = "result-b", stderr = "", exitCode = 0, timedOut = false }
        };
        var responses = await Task.WhenAll(candidates.Select(body => http.PostAsJsonAsync(url, body)));
        Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var verify = new CommandLineMcpDbContext(fixture.Options);
        var saved = await verify.Jobs.SingleAsync(j => j.Id == fixture.JobId);
        Assert.IsTrue(saved.Stdout is "result-a" or "result-b");
        var winner = new { stdout = saved.Stdout!, stderr = saved.Stderr!, exitCode = saved.ExitCode, timedOut = false };
        var loser = new { stdout = saved.Stdout == "result-a" ? "result-b" : "result-a", stderr = "", exitCode = 0, timedOut = false };
        Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsJsonAsync(url, winner)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await http.PostAsJsonAsync(url, loser)).StatusCode);
    }

    [TestMethod]
    public async Task PollExpiresStaleExecutionAndSupportsMissingWorkingDirectory()
    {
        using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        var pendingId = await fixture.AddJobAsync("Get-Date", workingDirectory: null);
        await using (var db = new CommandLineMcpDbContext(fixture.Options))
        {
            await db.Jobs.Where(j => j.Id == fixture.JobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow.AddMinutes(-5)));
        }

        using var factory = fixture.CreateFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        var response = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<PollResponse>();
        Assert.IsNotNull(claim);
        Assert.AreEqual(pendingId, claim.JobId);
        Assert.IsNull(claim.WorkingDirectory);
        await using var verify = new CommandLineMcpDbContext(fixture.Options);
        Assert.AreEqual(JobStatus.Unknown, (await verify.Jobs.SingleAsync(j => j.Id == fixture.JobId)).Status);
        Assert.AreEqual(JobStatus.Running, (await verify.Jobs.SingleAsync(j => j.Id == pendingId)).Status);
    }

    private sealed record PollResponse(Guid JobId, string Command, string WorkingDirectory, int TimeoutSeconds);

    private sealed class ApiFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.ApiTests", Guid.NewGuid().ToString("N"));
        public string Key { get; } = "test-client-bearer-key";
        public string ConnectionString { get; }
        public DbContextOptions<CommandLineMcpDbContext> Options { get; }
        public Guid JobId { get; private set; }
        public Guid ForeignJobId { get; private set; }
        public Guid ClientId { get; private set; }

        public ApiFixture()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "test.db") }.ToString();
            Options = new DbContextOptionsBuilder<CommandLineMcpDbContext>().UseSqlite(ConnectionString).Options;
        }

        public async Task InitializeAsync()
        {
            await using var db = new CommandLineMcpDbContext(Options);
            await db.Database.MigrateAsync();
            var first = new Client { User = new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) }, Name = "worker", BearerKeyHash = Hash(Key) };
            var second = new Client { User = new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) }, Name = "other", BearerKeyHash = Hash("other-key") };
            var job = new Job { Client = first, Command = "Get-Location", WorkingDirectory = "C:\\work", TimeoutSeconds = 30 };
            var foreign = new Job { Client = second, Command = "echo", WorkingDirectory = "C:\\", TimeoutSeconds = 30 };
            db.AddRange(first, second, job, foreign);
            await db.SaveChangesAsync();
            JobId = job.Id;
            ForeignJobId = foreign.Id;
            ClientId = first.Id;
        }

        public async Task AddJobsAsync(string prefix, int count)
        {
            await using var db = new CommandLineMcpDbContext(Options);
            db.Jobs.AddRange(Enumerable.Range(0, count).Select(i => new Job
            {
                ClientId = ClientId,
                Command = $"{prefix}-{i}",
                WorkingDirectory = "C:\\work",
                TimeoutSeconds = 30
            }));
            await db.SaveChangesAsync();
        }

        public async Task<Guid> AddJobAsync(string command, string? workingDirectory)
        {
            await using var db = new CommandLineMcpDbContext(Options);
            var job = new Job { ClientId = ClientId, Command = command, WorkingDirectory = workingDirectory, TimeoutSeconds = 30 };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        }

        public WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Database", ConnectionString);
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = ConnectionString }));
            });

        private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
