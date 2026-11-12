using CommandLineMcp.Data;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Claims;

namespace CommandLineMcp.Server;

/// <summary>
/// MCP-инструменты для управления клиентами и заданиями текущего пользователя.
/// </summary>
[McpServerToolType]
public sealed class CommandTools
{
    /// <summary>
    /// Возвращает клиентов, принадлежащих текущему пользователю.
    /// </summary>
    [McpServerTool(Name = "list_clients", UseStructuredContent = true, ReadOnly = true)]
    [Description("Lists the authenticated user's clients. It never returns clients belonging to another user.")]
    public static async Task<ClientListResult> ListClients(
        ClaimsPrincipal principal, IClientRepository clients, IUserAccessValidator access,
        CancellationToken cancellationToken)
    {
        if (!TryUserId(principal, out var userId) || !await access.IsOAuthAllowedAsync(userId, cancellationToken))
            return new(false, "unauthorized", []);
        var result = await clients.FindByOwnerAsync(userId, cancellationToken);
        return new(true, null, result.Select(c => new ClientSummary(c.Id, c.Name, c.IsEnabled,
            c.OperatingSystem, c.OsVersion, c.Architecture, c.DefaultShell,
            string.IsNullOrWhiteSpace(c.ShellsJson) ? [] :
                System.Text.Json.JsonSerializer.Deserialize<AvailableShell[]>(c.ShellsJson) ?? [],
            c.CapabilitiesUpdatedAt, c.LastSeenAt,
            c.IsEnabled && c.LastSeenAt is { } lastSeen &&
                lastSeen >= DateTime.UtcNow.AddSeconds(-45))).ToArray());
    }

    /// <summary>
    /// Ставит команду в очередь для собственного клиента пользователя.
    /// </summary>
    [McpServerTool(Name = "submit_job", UseStructuredContent = true, Destructive = true, OpenWorld = true)]
    [Description("Queues a command for one of your clients to execute. Commands run with that client's operating-system permissions; submit only commands you intend to execute.")]
    public static async Task<JobSubmissionResult> SubmitJob(
        ClaimsPrincipal principal, IClientRepository clients, IJobRepository jobs, IUserAccessValidator access,
        [Description("ID of one of your enabled clients.")] Guid clientId,
        [Description("The command the client should execute.")] string command,
        [Description("Optional working directory on the client.")] string? workingDirectory,
        [Description("Execution timeout in seconds; must be greater than zero.")] int timeoutSeconds,
        CancellationToken cancellationToken,
        [Description("Optional shell ID from list_clients (pwsh, powershell, cmd, bash or sh). When omitted, uses the client's default shell.")] string? shell = null)
    {
        if (!TryUserId(principal, out var userId) || !await access.IsOAuthAllowedAsync(userId, cancellationToken))
            return new(false, "unauthorized", null);
        if (string.IsNullOrWhiteSpace(command) || timeoutSeconds <= 0)
            return new(false, "invalid_input", null);
        var owned = await clients.FindByOwnerAsync(userId, cancellationToken);
        var target = owned.FirstOrDefault(c => c.Id == clientId && c.IsEnabled);
        if (target is null)
            return new(false, "client_not_found", null);
        if (shell is not null)
        {
            if (shell.Length is < 1 or > 32 || shell.Trim() != shell || target.ShellsJson is null)
                return new(false, "unsupported_shell", null);
            var available = System.Text.Json.JsonSerializer.Deserialize<AvailableShell[]>(target.ShellsJson);
            if (available is null || !available.Any(s => s.Id == shell))
                return new(false, "unsupported_shell", null);
        }
        var job = await jobs.CreateAsync(clientId, command, workingDirectory, timeoutSeconds,
            cancellationToken, shell: shell);
        return new(true, null, job.Id);
    }

    /// <summary>Queues an authenticated restart for an owned enabled client.</summary>
    [McpServerTool(Name = "restart_client", UseStructuredContent = true, Destructive = true)]
    [Description("Queues a graceful restart of the selected client. The client acknowledges the job, closes its journal, then starts a fresh Python interpreter loading current files from disk. Use after deploying updated client code.")]
    public static async Task<JobSubmissionResult> RestartClient(
        ClaimsPrincipal principal, IClientRepository clients, IJobRepository jobs, IUserAccessValidator access,
        [Description("ID of one of your enabled clients.")] Guid clientId,
        CancellationToken cancellationToken)
    {
        if (!TryUserId(principal, out var userId) || !await access.IsOAuthAllowedAsync(userId, cancellationToken))
            return new(false, "unauthorized", null);
        var owned = await clients.FindByOwnerAsync(userId, cancellationToken);
        if (owned.All(c => c.Id != clientId || !c.IsEnabled))
            return new(false, "client_not_found", null);
        var job = await jobs.CreateAsync(clientId, "__mcp_restart__", null, 60,
            cancellationToken, JobKind.Restart);
        return new(true, null, job.Id);
    }

    /// <summary>
    /// Возвращает состояние и результат принадлежащего пользователю задания.
    /// </summary>
    [McpServerTool(Name = "get_job", UseStructuredContent = true, ReadOnly = true)]
    [Description("Gets the status and any available stdout, stderr, and exit code for one of your jobs.")]
    public static async Task<JobResult> GetJob(
        ClaimsPrincipal principal, IJobRepository jobs, IUserAccessValidator access,
        [Description("ID returned by submit_job.")] Guid jobId, CancellationToken cancellationToken)
    {
        if (!TryUserId(principal, out var userId) || !await access.IsOAuthAllowedAsync(userId, cancellationToken))
            return JobResult.Failure("unauthorized");
        var job = await jobs.FindOwnedByIdAsync(userId, jobId, cancellationToken);
        return job is null ? JobResult.Failure("job_not_found") : JobResult.From(job);
    }

    /// <summary>
    /// Ожидает до 25 секунд изменения состояния задания и возвращает последнее состояние.
    /// </summary>
    [McpServerTool(Name = "wait_job", UseStructuredContent = true, ReadOnly = true)]
    [Description("Waits briefly for one of your jobs to finish, for at most 25 seconds, then returns its current status and any result.")]
    public static async Task<JobResult> WaitJob(
        ClaimsPrincipal principal, IJobRepository jobs, IUserAccessValidator access,
        [Description("ID returned by submit_job.")] Guid jobId,
        [Description("Maximum wait in seconds, from 0 to 25.")] int waitSeconds,
        CancellationToken cancellationToken)
    {
        if (!TryUserId(principal, out var userId) || !await access.IsOAuthAllowedAsync(userId, cancellationToken))
            return JobResult.Failure("unauthorized");
        if (waitSeconds is < 0 or > 25) return JobResult.Failure("invalid_wait_seconds");
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        OwnedJob? job;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            job = await jobs.FindOwnedByIdAsync(userId, jobId, cancellationToken);
            if (job is null) return JobResult.Failure("job_not_found");
            if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut or JobStatus.Unknown or JobStatus.Cancelled ||
                DateTime.UtcNow >= deadline) return JobResult.From(job);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        } while (true);
    }

    private static bool TryUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value, out userId);

    /// <summary>
    /// Представляет список собственных клиентов.
    /// </summary>
    public sealed record ClientListResult(bool Success, string? Error, IReadOnlyList<ClientSummary> Clients);

    /// <summary>
    /// Представляет краткие сведения о клиенте.
    /// </summary>
    public sealed record ClientSummary(Guid Id, string Name, bool IsEnabled,
        string? OperatingSystem, string? OsVersion, string? Architecture,
        string? DefaultShell, IReadOnlyList<AvailableShell> Shells,
        DateTime? CapabilitiesUpdatedAt, DateTime? LastSeenAt, bool IsOnline);

    public sealed record AvailableShell(string Id, string? Version);

    /// <summary>
    /// Представляет результат постановки задания.
    /// </summary>
    public sealed record JobSubmissionResult(bool Success, string? Error, Guid? JobId);


    /// <summary>
    /// Представляет состояние задания и полученный результат выполнения.
    /// </summary>
    public sealed record JobResult(bool Success, string? Error, Guid? JobId, string? Status,
        string? Stdout, string? Stderr, int? ExitCode)
    {
        /// <summary>
        /// Создаёт ответ об ошибке инструмента.
        /// </summary>
        public static JobResult Failure(string error) => new(false, error, null, null, null, null, null);

        /// <summary>
        /// Создаёт ответ из задания, принадлежащего текущему пользователю.
        /// </summary>
        public static JobResult From(OwnedJob job) => new(true, null, job.Id, job.Status.ToString(),
            job.Stdout, job.Stderr, job.ExitCode);
    }
}
