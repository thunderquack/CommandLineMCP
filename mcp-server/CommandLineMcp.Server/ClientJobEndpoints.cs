using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CommandLineMcp.Data;
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

        var now = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Jobs
            .Where(j => j.Id == jobId && j.ClientId == client.Id &&
                (j.Status == JobStatus.Running || j.Status == JobStatus.Unknown))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.Stdout, body.Stdout)
                .SetProperty(j => j.Stderr, body.Stderr)
                .SetProperty(j => j.ExitCode, body.ExitCode)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (changed == 0)
        {
            await db.Entry(job).ReloadAsync(ct);
            await transaction.CommitAsync(ct);
            return job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut
                ? SameResult(job, body, status) ? Results.Ok() : Results.Conflict()
                : Results.Conflict();
        }
        await transaction.CommitAsync(ct);
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
        await db.Jobs
            .Where(j => j.ClientId == clientId && j.Status == JobStatus.Running &&
                j.UpdatedAt.AddSeconds((double)j.TimeoutSeconds + UnknownGraceSeconds) <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Unknown)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }

    public static async Task ExpireAllStaleRunningAsync(CommandLineMcpDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Jobs
            .Where(j => j.Status == JobStatus.Running &&
                j.UpdatedAt.AddSeconds((double)j.TimeoutSeconds + UnknownGraceSeconds) <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Unknown)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }

    private static async Task<Job?> TryClaimAsync(CommandLineMcpDbContext db, Guid clientId, CancellationToken ct)
    {
        // Keep candidate selection, conditional claim, and read in one transaction so no
        // poller can observe a half-completed claim or claim a second running job.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var candidateIds = db.Jobs.AsNoTracking()
            .Where(j => j.ClientId == clientId && j.Status == JobStatus.Pending)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .Select(j => j.Id).Take(1);
        var now = DateTime.UtcNow;
        var changed = await db.Jobs
            .Where(j => candidateIds.Contains(j.Id) && j.ClientId == clientId && j.Status == JobStatus.Pending &&
                !db.Jobs.Any(r => r.ClientId == clientId && r.Status == JobStatus.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (changed != 1)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        // The conditional update guarantees there was no prior Running row, so the
        // resulting client-scoped Running row is the job this transaction claimed.
        var claimed = await db.Jobs.AsNoTracking()
            .SingleAsync(j => j.ClientId == clientId && j.Status == JobStatus.Running, ct);
        await transaction.CommitAsync(ct);
        return claimed;
    }

    public sealed record PollRequest([property: JsonPropertyName("waitSeconds")] int WaitSeconds);
    public sealed record JobResponse(Guid JobId, string Command, string? WorkingDirectory, int TimeoutSeconds);
    public sealed record ResultRequest(
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("exitCode")] int? ExitCode,
        [property: JsonPropertyName("timedOut")] bool TimedOut);
}
