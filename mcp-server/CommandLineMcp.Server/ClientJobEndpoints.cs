using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CommandLineMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CommandLineMcp.Server;

public static class ClientJobEndpoints
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const int UnknownGraceSeconds = 120;

    public static IEndpointRouteBuilder MapClientJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/client/jobs/poll", PollAsync);
        endpoints.MapPost("/api/client/jobs/{jobId:guid}/result", SubmitResultAsync);
        return endpoints;
    }

    private static async Task<IResult> PollAsync(HttpRequest request, PollRequest? body, CommandLineMcpDbContext db, CancellationToken ct)
    {
        if (!TryBearer(request, out var key)) return Results.Unauthorized();
        if (body is null || body.WaitSeconds is < 0 or > 30) return Results.BadRequest();
        var client = await FindClientAsync(db, key, ct);
        if (client is null) return Results.Unauthorized();
        var until = DateTime.UtcNow.AddSeconds(body.WaitSeconds);
        do
        {
            ct.ThrowIfCancellationRequested();
            await ExpireStaleRunningAsync(db, client.Id, ct);
            var claimed = await TryClaimAsync(db, client.Id, ct);
            if (claimed is not null)
                return Results.Ok(new JobResponse(claimed.Id, claimed.Command!,
                    claimed.WorkingDirectory, claimed.TimeoutSeconds));
            if (DateTime.UtcNow >= until) break;
            await Task.Delay(PollInterval, ct);
        } while (true);
        return Results.NoContent();
    }

    private static async Task<IResult> SubmitResultAsync(Guid jobId, HttpRequest request, ResultRequest? body,
        CommandLineMcpDbContext db, CancellationToken ct)
    {
        if (!TryBearer(request, out var key)) return Results.Unauthorized();
        if (body is null || body.Stdout is null || body.Stderr is null) return Results.BadRequest();
        var client = await FindClientAsync(db, key, ct);
        if (client is null) return Results.Unauthorized();
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.ClientId == client.Id, ct);
        if (job is null) return Results.NotFound();
        var status = body.TimedOut ? JobStatus.TimedOut : body.ExitCode == 0 ? JobStatus.Succeeded : JobStatus.Failed;
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut)
            return SameResult(job, body, status) ? Results.Ok() : Results.Conflict();
        if (job.Status is not (JobStatus.Running or JobStatus.Unknown)) return Results.Conflict();

        var changed = await db.Database.ExecuteSqlAsync($"""
            UPDATE Jobs SET Status = {status.ToString()}, Stdout = {body.Stdout}, Stderr = {body.Stderr},
                ExitCode = {body.ExitCode}, UpdatedAt = {DateTime.UtcNow.ToString("O")}
            WHERE lower(Id) = lower({jobId.ToString()}) AND lower(ClientId) = lower({client.Id.ToString()})
              AND Status IN ('Running', 'Unknown')
            """, ct);
        if (changed == 0)
        {
            await db.Entry(job).ReloadAsync(ct);
            return job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut
                ? SameResult(job, body, status) ? Results.Ok() : Results.Conflict()
                : Results.Conflict();
        }
        return Results.Ok();
    }

    private static bool SameResult(Job job, ResultRequest body, JobStatus status) =>
        job.Status == status && job.Stdout == body.Stdout && job.Stderr == body.Stderr && job.ExitCode == body.ExitCode;

    private static async Task<Client?> FindClientAsync(CommandLineMcpDbContext db, string key, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var candidates = await db.Clients.AsNoTracking()
            .Where(c => c.BearerKeyHash.ToUpper() == hash)
            .ToListAsync(ct);
        var hashBytes = Convert.FromHexString(hash);
        return candidates.FirstOrDefault(c => IsHexHash(c.BearerKeyHash) &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(c.BearerKeyHash), hashBytes));
    }

    private static bool IsHexHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryBearer(HttpRequest request, out string key)
    {
        key = string.Empty;
        var value = request.Headers["Authorization"].ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        key = value[7..].Trim();
        return key.Length > 0;
    }

    private static async Task ExpireStaleRunningAsync(CommandLineMcpDbContext db, Guid clientId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE Jobs SET Status = 'Unknown', UpdatedAt = {now.ToString("O")}
            WHERE lower(ClientId) = lower({clientId.ToString()}) AND Status = 'Running'
              AND julianday(UpdatedAt) <= julianday({now.ToString("O")})
                    - ((TimeoutSeconds + {UnknownGraceSeconds}) / 86400.0)
            """, ct);
    }

    public static async Task ExpireAllStaleRunningAsync(CommandLineMcpDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE Jobs SET Status = 'Unknown', UpdatedAt = {now.ToString("O")}
            WHERE Status = 'Running'
              AND julianday(UpdatedAt) <= julianday({now.ToString("O")})
                    - ((TimeoutSeconds + {UnknownGraceSeconds}) / 86400.0)
            """, ct);
    }

    private static async Task<Job?> TryClaimAsync(CommandLineMcpDbContext db, Guid clientId, CancellationToken ct)
    {
        // A single conditional UPDATE is the claim lock: SQLite serializes writers, so concurrent
        // pollers cannot transition more than one job to Running for this client.
        await using var connection = new SqliteConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Jobs SET Status = 'Running', UpdatedAt = $now
            WHERE Id = (SELECT Id FROM Jobs WHERE lower(ClientId) = lower($clientId) AND Status = 'Pending'
                        AND Command IS NOT NULL
                        ORDER BY CreatedAt, Id LIMIT 1)
              AND NOT EXISTS (SELECT 1 FROM Jobs WHERE lower(ClientId) = lower($clientId) AND Status = 'Running')
            RETURNING Id, Command, WorkingDirectory, TimeoutSeconds
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$clientId", clientId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Job
        {
            Id = Guid.Parse(reader.GetString(0)),
            Command = reader.IsDBNull(1) ? throw new InvalidOperationException("Claimed job has no command.") : reader.GetString(1),
            WorkingDirectory = reader.IsDBNull(2) ? null : reader.GetString(2),
            TimeoutSeconds = reader.GetInt32(3)
        };
    }

    public sealed record PollRequest([property: JsonPropertyName("waitSeconds")] int WaitSeconds);
    public sealed record JobResponse(Guid JobId, string Command, string? WorkingDirectory, int TimeoutSeconds);
    public sealed record ResultRequest(
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("exitCode")] int? ExitCode,
        [property: JsonPropertyName("timedOut")] bool TimedOut);
}
