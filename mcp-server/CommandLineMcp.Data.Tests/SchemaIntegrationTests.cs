using CommandLineMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class SchemaIntegrationTests
{
    [TestMethod]
    public async Task MigrationsApplyFromInitialAndAreRepeatable()
    {
        using var database = new TemporaryDatabase();
        var options = database.Options;
        await using var context = new CommandLineMcpDbContext(options);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync("20261112000000_Initial");
        CollectionAssert.AreEqual(new[] { "20261112000000_Initial" }, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        await context.Database.MigrateAsync();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.AreEqual(3, applied.Length);
        await context.Database.MigrateAsync();
        CollectionAssert.AreEqual(applied, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.IsFalse(context.Database.HasPendingModelChanges(), "The model snapshot must match the current EF model.");
    }

    [TestMethod]
    public async Task DefaultsUniqueKeysAndRestrictForeignKeysAreEnforced()
    {
        using var database = new TemporaryDatabase();
        await using var context = new CommandLineMcpDbContext(database.Options);
        await context.Database.MigrateAsync();

        var user = new User { TelegramId = 4001 };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        Assert.IsFalse(user.IsOAuthAllowed);

        var client = new Client { UserId = user.Id, Name = "agent", BearerKeyHash = new string('a', 64) };
        context.Clients.Add(client);
        await context.SaveChangesAsync();
        context.Clients.Add(new Client { UserId = user.Id, Name = "other", BearerKeyHash = new string('a', 64) });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        context.Users.Add(new User { TelegramId = 4001 });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        var invalidClient = new Client { UserId = Guid.NewGuid(), Name = "orphan", BearerKeyHash = new string('c', 64) };
        context.Clients.Add(invalidClient);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        var persistedClient = await context.Clients.SingleAsync(x => x.Id == client.Id);
        context.Jobs.Add(new Job { Client = persistedClient, Command = "Get-Location", TimeoutSeconds = 10 });
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlAsync($"DELETE FROM Clients WHERE Id = {client.Id}"));
    }

    [TestMethod]
    public async Task JobChecksAndResultFieldsPersistWithJobIdentity()
    {
        using var database = new TemporaryDatabase();
        await using var context = new CommandLineMcpDbContext(database.Options);
        await context.Database.MigrateAsync();
        var user = new User { TelegramId = 4100 };
        var client = new Client { User = user, Name = "worker", BearerKeyHash = new string('d', 64) };
        context.Users.Add(user);
        context.Clients.Add(client);
        await context.SaveChangesAsync();

        var job = new Job { Client = client, Command = "echo ok", WorkingDirectory = "C:\\", TimeoutSeconds = 12 };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        Assert.AreEqual(JobStatus.Pending, job.Status);

        context.Jobs.Add(new Job { ClientId = client.Id, TimeoutSeconds = 0 });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Jobs (Id, ClientId, TimeoutSeconds, Status, CreatedAt, UpdatedAt) VALUES ($id, $clientId, 1, 'Bogus', $now, $now)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$clientId", client.Id.ToString());
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }

        await using var verify = new CommandLineMcpDbContext(database.Options);
        var saved = await verify.Jobs.SingleAsync(x => x.Id == job.Id);
        saved.Status = JobStatus.Succeeded;
        saved.Stdout = "ok\n";
        saved.Stderr = string.Empty;
        saved.ExitCode = 0;
        await verify.SaveChangesAsync();
        await using var readback = new CommandLineMcpDbContext(database.Options);
        var result = await readback.Jobs.SingleAsync(x => x.Id == job.Id);
        Assert.AreEqual(job.Id, result.Id);
        Assert.AreEqual("ok\n", result.Stdout);
        Assert.AreEqual(string.Empty, result.Stderr);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(JobStatus.Succeeded, result.Status);
    }

    [TestMethod]
    public async Task AuditTimestampsAreUtcForSyncAndAsyncSavesAndCreatedAtIsPreserved()
    {
        using var database = new TemporaryDatabase();
        await using var context = new CommandLineMcpDbContext(database.Options);
        await context.Database.MigrateAsync();
        var user = new User { TelegramId = 4200, CreatedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch };
        context.Users.Add(user);
        context.SaveChanges();
        var created = user.CreatedAt;
        Assert.AreEqual(DateTimeKind.Utc, user.CreatedAt.Kind);
        Assert.AreEqual(DateTimeKind.Utc, user.UpdatedAt.Kind);
        Assert.AreNotEqual(DateTime.UnixEpoch, created);

        user.IsOAuthAllowed = true;
        context.SaveChanges();
        Assert.AreEqual(created, user.CreatedAt);
        Assert.AreEqual(DateTimeKind.Utc, user.UpdatedAt.Kind);

        user.IsOAuthAllowed = false;
        await context.SaveChangesAsync();
        Assert.AreEqual(created, user.CreatedAt);
        Assert.AreEqual(DateTimeKind.Utc, user.UpdatedAt.Kind);
    }

    [TestMethod]
    public async Task TransactionRollbackDoesNotPersistChanges()
    {
        using var database = new TemporaryDatabase();
        await using (var context = new CommandLineMcpDbContext(database.Options))
        {
            await context.Database.MigrateAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Users.Add(new User { TelegramId = 4300 });
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verify = new CommandLineMcpDbContext(database.Options);
        Assert.AreEqual(0, await verify.Users.CountAsync());
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.Tests", Guid.NewGuid().ToString("N"));
        public string ConnectionString { get; }
        public DbContextOptions<CommandLineMcpDbContext> Options { get; }

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "test.db") }.ToString();
            Options = new DbContextOptionsBuilder<CommandLineMcpDbContext>().UseSqlite(ConnectionString).Options;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}






