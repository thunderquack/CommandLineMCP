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
    public async Task StdinRotationUpdatesOwnedClientAndReactivatesIt()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var user = new User { TelegramId = 7002 };
        var other = new User { TelegramId = 7003 };
        db.Users.AddRange(user, other);
        await db.SaveChangesAsync();
        var client = new Client { UserId = user.Id, Name = "worker", BearerKeyHash = new string('A', 64), IsEnabled = false };
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
