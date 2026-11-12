using CommandLineMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CommandLineMcp.Admin;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
        {
            Console.Out.WriteLine(AdminCommandRunner.HelpText);
            return 0;
        }

        if (args.Length > 0 && args[0] == "auth")
        {
            return await AuthSecretsInitializer.RunAsync(args, Console.Out, Console.Error);
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

            var services = new ServiceCollection();
            services.AddDbContext<CommandLineMcpDbContext>(options => options.UseSqlite(connectionString));
            services.AddCommandLineMcpDataRepositories();
            await using var provider = services.BuildServiceProvider();
            var users = provider.GetRequiredService<IUserRepository>();
            var clients = provider.GetRequiredService<IClientRepository>();
            var database = provider.GetRequiredService<IDatabaseMaintenance>();
            return await AdminCommandRunner.RunAsync(args, users, clients, database, Console.Out, Console.Error);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Database operation failed.");
            return 1;
        }
    }
}
