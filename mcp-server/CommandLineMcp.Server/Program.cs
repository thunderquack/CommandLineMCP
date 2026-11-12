using CommandLineMcp.Data;
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

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CommandLineMcpDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGet("/health", async (CommandLineMcpDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "healthy", database = "reachable" })
            : Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: 503);
    }
    catch
    {
        return Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: 503);
    }
});

app.Run();
