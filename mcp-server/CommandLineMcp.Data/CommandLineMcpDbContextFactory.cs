using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CommandLineMcp.Data;

public sealed class CommandLineMcpDbContextFactory : IDesignTimeDbContextFactory<CommandLineMcpDbContext>
{
    public CommandLineMcpDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Data Source=data/commandline-mcp.db";
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new DbContextOptionsBuilder<CommandLineMcpDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new CommandLineMcpDbContext(options);
    }
}
