using System.Globalization;
using CommandLineMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class AdminCommandRunnerTests
{
    [TestMethod]
    public async Task AllowCreatesOrUpdatesWithoutChangingRoleAndDenyPreservesRole()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        Assert.AreEqual(0, await RunAsync(["users", "allow-oauth", "1001", "1002"], db, stdout, stderr));
        var created = await db.Users.OrderBy(x => x.TelegramId).ToListAsync();
        Assert.AreEqual(2, created.Count);
        Assert.IsTrue(created.All(x => x.IsOAuthAllowed && x.RoleId == 0));

        created[0].RoleId = 17;
        created[0].IsOAuthAllowed = false;
        created[1].RoleId = 23;
        await db.SaveChangesAsync();
        Assert.AreEqual(0, await RunAsync(["users", "allow-oauth", "1001"], db, stdout, stderr));
        Assert.AreEqual(17, (await db.Users.SingleAsync(x => x.TelegramId == 1001)).RoleId);
        Assert.IsTrue((await db.Users.SingleAsync(x => x.TelegramId == 1001)).IsOAuthAllowed);
        var afterAllow = (await db.Users.SingleAsync(x => x.TelegramId == 1001)).UpdatedAt;
        Assert.AreEqual(0, await RunAsync(["users", "allow-oauth", "1001"], db, stdout, stderr));
        Assert.AreEqual(afterAllow, (await db.Users.SingleAsync(x => x.TelegramId == 1001)).UpdatedAt);

        Assert.AreEqual(0, await RunAsync(["users", "deny-oauth", "1001", "1002"], db, stdout, stderr));
        var denied = await db.Users.OrderBy(x => x.TelegramId).ToListAsync();
        Assert.IsFalse(denied.Any(x => x.IsOAuthAllowed));
        CollectionAssert.AreEqual(new[] { 17, 23 }, denied.Select(x => x.RoleId).ToArray());
        var afterDeny = denied[0].UpdatedAt;
        Assert.AreEqual(0, await RunAsync(["users", "deny-oauth", "1001", "1002"], db, stdout, stderr));
        Assert.AreEqual(afterDeny, (await db.Users.SingleAsync(x => x.TelegramId == 1001)).UpdatedAt);
    }

    [TestMethod]
    public async Task SetRoleDoesNotChangeOAuthPermissionAndRepeatedUpdatesKeepUpdatedAt()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        db.Users.Add(new User { TelegramId = 2001, IsOAuthAllowed = true, RoleId = 5 });
        await db.SaveChangesAsync();
        var originalUpdatedAt = (await db.Users.SingleAsync()).UpdatedAt;
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        Assert.AreEqual(0, await RunAsync(["users", "set-role", "2001", "0"], db, stdout, stderr));
        var updated = await db.Users.SingleAsync();
        Assert.AreEqual(0, updated.RoleId);
        Assert.IsTrue(updated.IsOAuthAllowed);
        var changedUpdatedAt = updated.UpdatedAt;
        Assert.AreEqual(DateTimeKind.Utc, changedUpdatedAt.Kind);

        Assert.AreEqual(0, await RunAsync(["users", "set-role", "2001", "0"], db, stdout, stderr));
        Assert.AreEqual(changedUpdatedAt, (await db.Users.SingleAsync()).UpdatedAt);
        Assert.AreNotEqual(originalUpdatedAt, changedUpdatedAt);
    }

    [TestMethod]
    public async Task InvalidArgumentsAndMissingUserReturnDocumentedExitCodesWithoutWrites()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        db.Users.Add(new User { TelegramId = 3001, IsOAuthAllowed = false, RoleId = 8 });
        await db.SaveChangesAsync();
        var before = await db.Users.SingleAsync();
        var updatedAt = before.UpdatedAt;
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        Assert.AreEqual(0, await RunAsync(["users", "allow-oauth", "9999"], db, stdout, stderr));
        var created = await db.Users.SingleAsync(x => x.TelegramId == 9999);
        Assert.IsTrue(created.IsOAuthAllowed);
        Assert.AreEqual(0, created.RoleId);
        Assert.AreEqual(3, await RunAsync(["users", "deny-oauth", "9998"], db, stdout, stderr));
        Assert.AreEqual(2, await RunAsync(["users", "allow-oauth"], db, stdout, stderr));
        Assert.AreEqual(2, await RunAsync(["users", "show", "0"], db, stdout, stderr));
        Assert.AreEqual(2, await RunAsync(["users", "deny-oauth", "9223372036854775808"], db, stdout, stderr));
        Assert.AreEqual(2, await RunAsync(["users", "set-role", "3001", "01"], db, stdout, stderr));
        Assert.AreEqual(2, await RunAsync(["users", "set-role", "3001", "2"], db, stdout, stderr));

        db.ChangeTracker.Clear();
        var after = await db.Users.SingleAsync(x => x.TelegramId == 3001);
        Assert.IsFalse(after.IsOAuthAllowed);
        Assert.AreEqual(8, after.RoleId);
        Assert.AreEqual(updatedAt, after.UpdatedAt);
    }

    [TestMethod]
    public async Task BatchValidationIsAtomicAndValidatesAllIdsBeforeWriting()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        db.Users.Add(new User { TelegramId = 4001, IsOAuthAllowed = false, RoleId = 9 });
        await db.SaveChangesAsync();
        var before = (await db.Users.SingleAsync()).UpdatedAt;
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        Assert.AreEqual(2, await RunAsync(["users", "allow-oauth", "4001", "0"], db, stdout, stderr));
        db.ChangeTracker.Clear();
        var saved = await db.Users.SingleAsync();
        Assert.IsFalse(saved.IsOAuthAllowed);
        Assert.AreEqual(9, saved.RoleId);
        Assert.AreEqual(before, saved.UpdatedAt);
    }

    [TestMethod]
    public async Task ShowPrintsUpdatedAtInUtc()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        db.Users.Add(new User { TelegramId = 5001, IsOAuthAllowed = true, RoleId = 4 });
        await db.SaveChangesAsync();
        var user = await db.Users.SingleAsync();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        Assert.AreEqual(0, await RunAsync(["users", "show", "5001"], db, stdout, stderr));
        var output = stdout.ToString();
        Assert.IsTrue(output.Contains("5001", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("true", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(output.Contains("4", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains(user.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("Z", StringComparison.Ordinal) || output.Contains("+00:00", StringComparison.Ordinal), "The displayed timestamp must carry an explicit UTC offset.");
    }

    [TestMethod]
    public async Task UpgradeFromPriorSchemaPreservesUsersAndDefaultsRoleToZero()
    {
        using var database = new TemporaryDatabase();
        await using (var old = database.CreateContext())
        {
            var migrator = old.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261112112444_RequireJobCommand");
        }
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Users (Id, TelegramId, IsOAuthAllowed, CreatedAt, UpdatedAt) VALUES ($id, $telegram, $allowed, $created, $updated)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$telegram", 6001L);
            command.Parameters.AddWithValue("$allowed", true);
            command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var user = await db.Users.SingleAsync(x => x.TelegramId == 6001);
        Assert.AreEqual(0, user.RoleId);
        Assert.IsTrue(user.IsOAuthAllowed);
    }

    private static Task<int> RunAsync(string[] args, CommandLineMcpDbContext db, TextWriter stdout, TextWriter stderr) =>
        AdminCommandRunner.RunAsync(args, new UserRepository(db), new DatabaseMaintenance(db), stdout, stderr);

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.AdminTests", Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}

