using CommandLineMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class ClientAdminCommandTests
{
    [TestMethod]
    public async Task CreateStoresOnlyHashAndPrintsGeneratedKeyOnce()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var user = new User { TelegramId = 7001 };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var code = await AdminCommandRunner.RunAsync(["clients", "create", "7001", "worker"],
            new UserRepository(db), new ClientRepository(db), new DatabaseMaintenance(db), stdout, stderr);

        Assert.AreEqual(0, code);
        Assert.AreEqual(string.Empty, stderr.ToString());
        StringAssert.Contains(stdout.ToString(), "Name=worker");
        var client = await db.Clients.SingleAsync();
        Assert.AreEqual(user.Id, client.UserId);
        Assert.IsTrue(client.IsEnabled);
        var key = stdout.ToString().Split("BearerKey=", StringSplitOptions.None)[1].Trim();
        Assert.AreEqual(43, key.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), client.BearerKeyHash);
        Assert.IsFalse(client.BearerKeyHash.Contains(key, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StdinRotationUpdatesOwnedClient()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var user = new User { TelegramId = 7002 };
        var other = new User { TelegramId = 7003 };
        db.Users.AddRange(user, other);
        await db.SaveChangesAsync();
        var client = new Client { UserId = user.Id, Name = "worker", BearerKeyHash = new string('A', 64) };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var key = new string('x', 32);

        var code = await AdminCommandRunner.RunAsync(["clients", "set-key", "7002", client.Id.ToString("D"), "--stdin"],
            new UserRepository(db), new ClientRepository(db), new DatabaseMaintenance(db), stdout, stderr,
            new StringReader(key + "\n"));

        Assert.AreEqual(0, code);
        StringAssert.Contains(stdout.ToString(), "KeyUpdated=true");
        Assert.IsFalse(stdout.ToString().Contains(key, StringComparison.Ordinal));
        Assert.IsFalse(stderr.ToString().Contains(key, StringComparison.Ordinal));
        db.ChangeTracker.Clear();
        var saved = await db.Clients.SingleAsync();
        Assert.IsTrue(saved.IsEnabled);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), saved.BearerKeyHash);
        var repository = new ClientRepository(db);
        Assert.AreEqual(1, (await repository.FindByBearerKeyHashCandidatesAsync(saved.BearerKeyHash)).Count);
        Assert.AreEqual(0, (await repository.FindByBearerKeyHashCandidatesAsync(new string('A', 64))).Count);
        var unchangedTimestamp = saved.UpdatedAt;
        var repeated = await repository.ReplaceBearerKeyHashAsync(user.Id, saved.Id, saved.BearerKeyHash);
        Assert.AreEqual(unchangedTimestamp, repeated!.UpdatedAt);
        Assert.AreEqual(3, await AdminCommandRunner.RunAsync(["clients", "set-key", "7003", client.Id.ToString("D")],
            new UserRepository(db), new ClientRepository(db), new DatabaseMaintenance(db), TextWriter.Null, stderr));
    }

    [TestMethod]
    public async Task SoftDeletePreservesJobsAndPreventsKeyReactivation()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var owner = new User { TelegramId = 7901 };
        var stranger = new User { TelegramId = 7902 };
        db.Users.AddRange(owner, stranger);
        await db.SaveChangesAsync();
        var client = new Client { UserId = owner.Id, Name = "retired",
            BearerKeyHash = new string('B', 64) };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var job = new Job { ClientId = client.Id, Command = "echo archived",
            TimeoutSeconds = 5, Status = JobStatus.Pending };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        var originalUpdated = client.UpdatedAt;
        var repository = new ClientRepository(db);

        Assert.IsFalse(await repository.DeleteAsync(stranger.Id, client.Id));
        Assert.IsTrue(await repository.DeleteAsync(owner.Id, client.Id));
        Assert.IsFalse(await repository.DeleteAsync(owner.Id, client.Id));

        db.ChangeTracker.Clear();
        var stored = await db.Clients.SingleAsync(c => c.Id == client.Id);
        Assert.IsNotNull(stored.DeletedAt);
        Assert.IsFalse(stored.IsEnabled);
        Assert.AreEqual(originalUpdated, stored.UpdatedAt);
        Assert.AreEqual(1, await db.Jobs.CountAsync(j => j.ClientId == client.Id));
        Assert.AreEqual(0, (await repository.FindByOwnerAsync(owner.Id)).Count);
        Assert.AreEqual(0, (await repository.FindByBearerKeyHashCandidatesAsync(stored.BearerKeyHash)).Count);
        Assert.IsNull(await repository.ReplaceBearerKeyHashAsync(owner.Id, client.Id, new string('C', 64)));
        Assert.IsNull(await repository.RenameAsync(owner.Id, client.Id, "new-name"));
        Assert.IsFalse(await repository.RevokeAsync(owner.Id, client.Id));
        Assert.IsFalse(await repository.TouchHeartbeatAsync(client.Id));
        Assert.IsFalse(await repository.UpdateCapabilitiesAsync(client.Id, "Windows", "10", "AMD64",
            "pwsh", "[]"));
    }

    [TestMethod]
    public async Task AdminDeleteSoftDeletesOwnedClientWithoutRemovingJobs()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var owner = new User { TelegramId = 7801 };
        var stranger = new User { TelegramId = 7802 };
        db.Users.AddRange(owner, stranger);
        await db.SaveChangesAsync();
        var client = new Client { UserId = owner.Id, Name = "retired",
            BearerKeyHash = new string('F', 64) };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.Jobs.Add(new Job { ClientId = client.Id, Command = "echo old",
            TimeoutSeconds = 30 });
        await db.SaveChangesAsync();

        var repository = new ClientRepository(db);
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var id = client.Id.ToString("D");

        var missing = await AdminCommandRunner.RunAsync(["clients", "delete", "7802", id],
            new UserRepository(db), repository, new DatabaseMaintenance(db), stdout, stderr);
        Assert.AreEqual(3, missing);
        Assert.IsNull((await db.Clients.AsNoTracking().SingleAsync(x => x.Id == client.Id)).DeletedAt);

        stdout.GetStringBuilder().Clear();
        var success = await AdminCommandRunner.RunAsync(["clients", "delete", "7801", id],
            new UserRepository(db), repository, new DatabaseMaintenance(db), stdout, stderr);
        Assert.AreEqual(0, success);
        StringAssert.Contains(stdout.ToString(), $"ClientId={id} Deleted=true");
        db.ChangeTracker.Clear();
        var saved = await db.Clients.SingleAsync(c => c.Id == client.Id);
        Assert.IsNotNull(saved.DeletedAt);
        Assert.IsFalse(saved.IsEnabled);
        Assert.AreEqual(1, await db.Jobs.CountAsync(j => j.ClientId == client.Id));

        var duplicate = await AdminCommandRunner.RunAsync(["clients", "delete", "7801", id],
            new UserRepository(db), repository, new DatabaseMaintenance(db), stdout, stderr);
        Assert.AreEqual(3, duplicate);

        var invalid = await AdminCommandRunner.RunAsync(["clients", "delete", "7801", "not-a-guid"],
            new UserRepository(db), repository, new DatabaseMaintenance(db), stdout, stderr);
        Assert.AreEqual(2, invalid);
        var forbiddenOption = await AdminCommandRunner.RunAsync(["clients", "delete", "7801", id, "--stdin"],
            new UserRepository(db), repository, new DatabaseMaintenance(db), stdout, stderr);
        Assert.AreEqual(2, forbiddenOption);
    }

    [TestMethod]
    public async Task InvalidStdinAndMissingOwnerAreRejectedWithoutKeyLeak()
    {
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        var clients = new Mock<IClientRepository>(MockBehavior.Strict);
        var database = new Mock<IDatabaseMaintenance>(MockBehavior.Strict);
        var error = new StringWriter();
        var bad = await AdminCommandRunner.RunAsync(["clients", "set-key", "42", Guid.NewGuid().ToString(), "--stdin"],
            users.Object, clients.Object, database.Object, TextWriter.Null, error, new StringReader("too short\n"));
        Assert.AreEqual(2, bad);
        StringAssert.Contains(error.ToString(), "Invalid key input.");
        database.VerifyNoOtherCalls();
        users.VerifyNoOtherCalls();
        clients.VerifyNoOtherCalls();

        database.Setup(x => x.MigrateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        users.Setup(x => x.FindByTelegramIdAsync(43, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        var missing = await AdminCommandRunner.RunAsync(["clients", "create", "43", "worker"],
            users.Object, clients.Object, database.Object, TextWriter.Null, error);
        Assert.AreEqual(3, missing);
        Assert.IsFalse(error.ToString().Contains("BearerKey=", StringComparison.Ordinal));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.ClientAdminTests", Guid.NewGuid().ToString("N"));
        public string ConnectionString { get; }

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "test.db") }.ToString();
        }

        public CommandLineMcpDbContext CreateContext() => new(new DbContextOptionsBuilder<CommandLineMcpDbContext>().UseSqlite(ConnectionString).Options);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
