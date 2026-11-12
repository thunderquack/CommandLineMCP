using CommandLineMcp.Data;
using CommandLineMcp.Server;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? "Data Source=data/commandline-mcp.db";
var databasePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
var databaseDirectory = Path.GetDirectoryName(databasePath);
if (!string.IsNullOrWhiteSpace(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}
builder.Services.AddDbContext<CommandLineMcpDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddCommandLineMcpDataRepositories();
builder.Services.AddHostedService<UnknownJobWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<IDatabaseMaintenance>();
    await database.MigrateAsync();
}

app.MapGet("/health", async (IDatabaseMaintenance database, CancellationToken cancellationToken) =>
{
    try
    {
        return await database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "healthy", database = "reachable" })
            : Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: 503);
    }
    catch
    {
        return Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: 503);
    }
});

app.MapClientJobEndpoints();

app.Run();

public partial class Program;
