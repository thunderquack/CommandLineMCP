using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CommandLineMcp.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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

    private sealed record PollResponse(Guid JobId, string Command, string WorkingDirectory, int TimeoutSeconds);

    private sealed class ApiFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.ApiTests", Guid.NewGuid().ToString("N"));
        public string Key { get; } = "test-client-bearer-key";
        public string ConnectionString { get; }
        public DbContextOptions<CommandLineMcpDbContext> Options { get; }
        public Guid JobId { get; private set; }
        public Guid ForeignJobId { get; private set; }

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
