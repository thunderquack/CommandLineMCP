using Microsoft.EntityFrameworkCore;

namespace CommandLineMcp.Data;

public interface IJobRepository
{
    Task<int> ExpireStaleRunningAsync(Guid? clientId, CancellationToken cancellationToken = default);
    Task<Job?> ClaimNextAsync(Guid clientId, CancellationToken cancellationToken = default);
    Task<JobResultSubmissionOutcome> SubmitResultAsync(Guid clientId, Guid jobId,
        JobResultSubmission result, CancellationToken cancellationToken = default);
}

public sealed record JobResultSubmission(string Stdout, string Stderr, int? ExitCode, bool TimedOut);

public enum JobResultSubmissionOutcome { Accepted, IdenticalRetry, Conflict, NotFound }

public interface IUserRepository
{
    Task<User?> FindByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default);
    Task AllowOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default);
    Task<UserBatchUpdateResult> DenyOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default);
    Task<UserBatchUpdateResult> SetRoleAsync(IReadOnlyCollection<long> telegramIds, int roleId, CancellationToken cancellationToken = default);
}

public sealed record UserBatchUpdateResult(IReadOnlyList<long> MissingTelegramIds)
{
    public bool Succeeded => MissingTelegramIds.Count == 0;
}

public interface IClientRepository
{
    Task<IReadOnlyList<Client>> FindByBearerKeyHashCandidatesAsync(string uppercaseSha256Hash,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseMaintenance
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}

public sealed class JobRepository(CommandLineMcpDbContext db) : IJobRepository
{
    private const int UnknownGraceSeconds = 120;

    public Task<int> ExpireStaleRunningAsync(Guid? clientId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = db.Jobs.Where(j => j.Status == JobStatus.Running &&
            j.UpdatedAt.AddSeconds((double)j.TimeoutSeconds + UnknownGraceSeconds) <= now);
        if (clientId.HasValue) query = query.Where(j => j.ClientId == clientId.Value);
        return query.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Unknown)
            .SetProperty(j => j.UpdatedAt, now), cancellationToken);
    }

    public async Task<Job?> ClaimNextAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var candidateIds = db.Jobs.AsNoTracking()
            .Where(j => j.ClientId == clientId && j.Status == JobStatus.Pending)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).Select(j => j.Id).Take(1);
        var now = DateTime.UtcNow;
        var changed = await db.Jobs.Where(j => candidateIds.Contains(j.Id) && j.ClientId == clientId &&
                j.Status == JobStatus.Pending &&
                !db.Jobs.Any(r => r.ClientId == clientId && r.Status == JobStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.UpdatedAt, now), cancellationToken);
        if (changed != 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var claimed = await db.Jobs.AsNoTracking()
            .SingleAsync(j => j.ClientId == clientId && j.Status == JobStatus.Running, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<JobResultSubmissionOutcome> SubmitResultAsync(Guid clientId, Guid jobId,
        JobResultSubmission result, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.ClientId == clientId, cancellationToken);
        if (job is null) return JobResultSubmissionOutcome.NotFound;
        var status = result.TimedOut ? JobStatus.TimedOut : result.ExitCode == 0 ? JobStatus.Succeeded : JobStatus.Failed;
        if (IsFinal(job.Status)) return SameResult(job, result, status)
            ? JobResultSubmissionOutcome.IdenticalRetry : JobResultSubmissionOutcome.Conflict;
        if (job.Status is not (JobStatus.Running or JobStatus.Unknown)) return JobResultSubmissionOutcome.Conflict;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var changed = await db.Jobs.Where(j => j.Id == jobId && j.ClientId == clientId &&
                (j.Status == JobStatus.Running || j.Status == JobStatus.Unknown))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status)
                .SetProperty(j => j.Stdout, result.Stdout).SetProperty(j => j.Stderr, result.Stderr)
                .SetProperty(j => j.ExitCode, result.ExitCode).SetProperty(j => j.UpdatedAt, now), cancellationToken);
        if (changed == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return JobResultSubmissionOutcome.Accepted;
        }

        await db.Entry(job).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return IsFinal(job.Status) && SameResult(job, result, status)
            ? JobResultSubmissionOutcome.IdenticalRetry : JobResultSubmissionOutcome.Conflict;
    }

    private static bool IsFinal(JobStatus status) => status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut;
    private static bool SameResult(Job job, JobResultSubmission result, JobStatus status) =>
        job.Status == status && job.Stdout == result.Stdout && job.Stderr == result.Stderr && job.ExitCode == result.ExitCode;
}

public sealed class UserRepository(CommandLineMcpDbContext db) : IUserRepository
{
    public Task<User?> FindByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.TelegramId == telegramId, cancellationToken);

    public async Task AllowOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default)
    {
        var ids = telegramIds.Distinct().ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var users = await db.Users.Where(x => ids.Contains(x.TelegramId)).ToListAsync(cancellationToken);
        var known = users.Select(x => x.TelegramId).ToHashSet();
        var changed = false;
        foreach (var user in users)
            if (!user.IsOAuthAllowed) { user.IsOAuthAllowed = true; changed = true; }
        var missing = ids.Where(id => !known.Contains(id)).ToArray();
        foreach (var id in missing)
            db.Users.Add(new User { TelegramId = id, IsOAuthAllowed = true, RoleId = 0 });
        if (changed || missing.Length != 0)
            await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<UserBatchUpdateResult> DenyOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default) =>
        UpdateExistingAsync(telegramIds, user => user.IsOAuthAllowed = false, cancellationToken);

    public Task<UserBatchUpdateResult> SetRoleAsync(IReadOnlyCollection<long> telegramIds, int roleId, CancellationToken cancellationToken = default) =>
        UpdateExistingAsync(telegramIds, user => user.RoleId = roleId, cancellationToken);

    private async Task<UserBatchUpdateResult> UpdateExistingAsync(IReadOnlyCollection<long> telegramIds,
        Action<User> update, CancellationToken cancellationToken)
    {
        var ids = telegramIds.Distinct().ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var users = await db.Users.Where(x => ids.Contains(x.TelegramId)).ToListAsync(cancellationToken);
        var found = users.Select(x => x.TelegramId).ToHashSet();
        var missing = ids.Where(x => !found.Contains(x)).ToArray();
        if (missing.Length != 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new UserBatchUpdateResult(missing);
        }
        var before = users.Select(x => (x.RoleId, x.IsOAuthAllowed)).ToArray();
        for (var i = 0; i < users.Count; i++) update(users[i]);
        if (users.Where((user, i) => user.RoleId != before[i].RoleId || user.IsOAuthAllowed != before[i].IsOAuthAllowed).Any())
            await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserBatchUpdateResult(Array.Empty<long>());
    }
}

public static class DataRepositoryServiceCollectionExtensions
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddCommandLineMcpDataRepositories(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IJobRepository, JobRepository>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IUserRepository, UserRepository>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IClientRepository, ClientRepository>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IDatabaseMaintenance, DatabaseMaintenance>(services);
        return services;
    }
}

public sealed class ClientRepository(CommandLineMcpDbContext db) : IClientRepository
{
    public async Task<IReadOnlyList<Client>> FindByBearerKeyHashCandidatesAsync(string uppercaseSha256Hash,
        CancellationToken cancellationToken = default) => await db.Clients.AsNoTracking()
        .Where(c => c.BearerKeyHash.ToUpper() == uppercaseSha256Hash).ToListAsync(cancellationToken);
}

public sealed class DatabaseMaintenance(CommandLineMcpDbContext db) : IDatabaseMaintenance
{
    public Task MigrateAsync(CancellationToken cancellationToken = default) => db.Database.MigrateAsync(cancellationToken);
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) => db.Database.CanConnectAsync(cancellationToken);
}
