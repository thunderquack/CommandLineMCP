using CommandLineMcp.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CommandLineMcp.Server;

public static class ClientJobEndpoints
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static IEndpointRouteBuilder MapClientJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/client/capabilities", PublishCapabilitiesAsync);
        endpoints.MapPost("/api/client/jobs/poll", PollAsync);
        endpoints.MapPost("/api/client/jobs/{jobId:guid}/result", SubmitResultAsync);
        return endpoints;
    }

    private static async Task<IResult> PublishCapabilitiesAsync(HttpRequest request,
        CapabilitiesRequest? body, IClientRepository clients, CancellationToken ct)
    {
        if (!TryBearer(request, out var key)) return Results.Unauthorized();
        var client = await FindClientAsync(clients, key, ct);
        if (client is null) return Results.Unauthorized();

        if (body is null || string.IsNullOrWhiteSpace(body.OperatingSystem)
            || body.OperatingSystem.Length > 64 || string.IsNullOrWhiteSpace(body.OsVersion)
            || body.OsVersion.Length > 256 || string.IsNullOrWhiteSpace(body.Architecture)
            || body.Architecture.Length > 64 || string.IsNullOrWhiteSpace(body.DefaultShell)
            || body.DefaultShell.Length > 32 || body.Shells is null
            || body.Shells.Length is < 1 or > 16
            || body.Shells.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)
                || s.Id.Length > 32 || (s.Version?.Length ?? 0) > 128)
            || body.Shells.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != body.Shells.Length
            || !body.Shells.Any(s => s.Id == body.DefaultShell))
            return Results.BadRequest();

        var json = System.Text.Json.JsonSerializer.Serialize(body.Shells);
        await clients.UpdateCapabilitiesAsync(client.Id, body.OperatingSystem,
            body.OsVersion, body.Architecture, body.DefaultShell, json, ct);
        return Results.Ok();
    }

    private static async Task<IResult> PollAsync(HttpRequest request, PollRequest? body,
        IClientRepository clients, IJobRepository jobs, CancellationToken ct)
    {
        if (!TryBearer(request, out var key)) return Results.Unauthorized();
        if (body is null || body.WaitSeconds is < 0 or > 30) return Results.BadRequest();
        var client = await FindClientAsync(clients, key, ct);
        if (client is null) return Results.Unauthorized();
        var until = DateTime.UtcNow.AddSeconds(body.WaitSeconds);
        do
        {
            ct.ThrowIfCancellationRequested();
            await jobs.ExpireStaleRunningAsync(client.Id, ct);
            var claimed = await jobs.ClaimNextAsync(client.Id, ct);
            if (claimed is not null)
                return Results.Ok(new JobResponse(claimed.Id, claimed.Command!,
                    claimed.WorkingDirectory, claimed.TimeoutSeconds));
            if (DateTime.UtcNow >= until) break;
            await Task.Delay(PollInterval, ct);
        } while (true);
        return Results.NoContent();
    }

    private static async Task<IResult> SubmitResultAsync(Guid jobId, HttpRequest request, ResultRequest? body,
        IClientRepository clients, IJobRepository jobs, CancellationToken ct)
    {
        if (!TryBearer(request, out var key)) return Results.Unauthorized();
        if (body is null || body.Stdout is null || body.Stderr is null) return Results.BadRequest();
        var client = await FindClientAsync(clients, key, ct);
        if (client is null) return Results.Unauthorized();
        var outcome = await jobs.SubmitResultAsync(client.Id, jobId,
            new JobResultSubmission(body.Stdout, body.Stderr, body.ExitCode, body.TimedOut), ct);
        return outcome switch
        {
            JobResultSubmissionOutcome.Accepted or JobResultSubmissionOutcome.IdenticalRetry => Results.Ok(),
            JobResultSubmissionOutcome.NotFound => Results.NotFound(),
            _ => Results.Conflict()
        };
    }

    private static async Task<Client?> FindClientAsync(IClientRepository clients, string key, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var candidates = await clients.FindByBearerKeyHashCandidatesAsync(hash, ct);
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

    public sealed record ShellCapability(string Id, string? Version);
    public sealed record CapabilitiesRequest(string OperatingSystem, string OsVersion,
        string Architecture, string DefaultShell, ShellCapability[] Shells);

    public sealed record PollRequest([property: JsonPropertyName("waitSeconds")] int WaitSeconds);
    public sealed record JobResponse(Guid JobId, string Command, string? WorkingDirectory, int TimeoutSeconds);
    public sealed record ResultRequest(
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("exitCode")] int? ExitCode,
        [property: JsonPropertyName("timedOut")] bool TimedOut);
}
