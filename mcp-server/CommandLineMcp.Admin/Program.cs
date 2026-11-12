using CommandLineMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
{
    Console.Out.WriteLine(AdminCommandRunner.HelpText);
    return 0;
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
    ?? "Data Source=data/commandline-mcp.db";
try
{
    var sqlite = new SqliteConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(sqlite.DataSource) && sqlite.DataSource != ":memory:")
    {
        var databasePath = Path.GetFullPath(sqlite.DataSource);
        var parent = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    var options = new DbContextOptionsBuilder<CommandLineMcpDbContext>()
        .UseSqlite(connectionString)
        .Options;
    await using var db = new CommandLineMcpDbContext(options);
    return await AdminCommandRunner.RunAsync(args, db, Console.Out, Console.Error);
}
catch (Exception)
{
    Console.Error.WriteLine("Database operation failed.");
    return 1;
}
