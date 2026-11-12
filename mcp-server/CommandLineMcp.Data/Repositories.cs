using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace CommandLineMcp.Data;

/// <summary>
/// Предоставляет операции получения и сохранения заданий клиентов.
/// </summary>
public interface IJobRepository
{
    Task<OwnedJob> CreateAsync(Guid clientId, string command, string? workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default, JobKind kind = JobKind.Command, string? shell = null);
    Task<OwnedJob?> FindOwnedByIdAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Переводит просроченные выполняющиеся задания в состояние с неизвестным исходом.
    /// </summary>
    /// <param name="clientId">Идентификатор клиента для ограничения поиска; <see langword="null"/> означает всех клиентов.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Количество заданий, переведённых в состояние <see cref="JobStatus.Unknown"/>.</returns>
    Task<int> ExpireStaleRunningAsync(Guid? clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно назначает клиенту самое раннее ожидающее задание, если у него нет выполняющегося задания.
    /// </summary>
    /// <param name="clientId">Идентификатор клиента, которому назначается задание.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Назначенное задание либо <see langword="null"/>, если ожидающих заданий нет или у клиента уже выполняется задание.</returns>
    Task<Job?> ClaimNextAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет результат задания, выполнявшегося или отмеченного как имеющего неизвестный исход.
    /// </summary>
    /// <param name="clientId">Идентификатор клиента, которому принадлежит задание.</param>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <param name="result">Полученные стандартный вывод, поток ошибок, код завершения и признак тайм-аута.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns><see cref="JobResultSubmissionOutcome.Accepted"/> при сохранении; <see cref="JobResultSubmissionOutcome.IdenticalRetry"/> при повторе того же результата; <see cref="JobResultSubmissionOutcome.Conflict"/> при несовпадении состояния или результата; <see cref="JobResultSubmissionOutcome.NotFound"/> если задание не принадлежит клиенту либо отсутствует.</returns>
    Task<JobResultSubmissionOutcome> SubmitResultAsync(Guid clientId, Guid jobId,
        JobResultSubmission result, CancellationToken cancellationToken = default);
}

public sealed record JobResultSubmission(string Stdout, string Stderr, int? ExitCode, bool TimedOut);

public enum JobResultSubmissionOutcome { Accepted, IdenticalRetry, Conflict, NotFound }

/// <summary>
/// Предоставляет поиск пользователей и пакетное изменение их настроек.
/// </summary>
public interface IUserRepository
{
    Task<User> FindOrCreateByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default);
    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Находит пользователя по Telegram ID.
    /// </summary>
    /// <param name="telegramId">Уникальный Telegram ID пользователя.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Пользователь либо <see langword="null"/>, если он не найден.</returns>
    Task<User?> FindByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Разрешает OAuth указанным пользователям, создавая отсутствующие записи.
    /// </summary>
    /// <param name="telegramIds">Telegram ID пользователей; повторяющиеся значения обрабатываются один раз.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Задача, завершающаяся после атомарного применения изменений.</returns>
    Task AllowOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Запрещает OAuth указанным существующим пользователям.
    /// </summary>
    /// <param name="telegramIds">Telegram ID пользователей; повторяющиеся значения обрабатываются один раз.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Итог пакетного изменения; если отсутствует хотя бы один пользователь, обновление не применяется и перечисляются все отсутствующие ID.</returns>
    Task<UserBatchUpdateResult> DenyOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Устанавливает роль указанным существующим пользователям.
    /// </summary>
    /// <param name="telegramIds">Telegram ID пользователей; повторяющиеся значения обрабатываются один раз.</param>
    /// <param name="roleId">Новый идентификатор роли.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Итог пакетного изменения; если отсутствует хотя бы один пользователь, обновление не применяется и перечисляются все отсутствующие ID.</returns>
    Task<UserBatchUpdateResult> SetRoleAsync(IReadOnlyCollection<long> telegramIds, int roleId, CancellationToken cancellationToken = default);
}

public sealed record UserBatchUpdateResult(IReadOnlyList<long> MissingTelegramIds)
{
    public bool Succeeded => MissingTelegramIds.Count == 0;
}

/// <summary>
/// Предоставляет поиск клиентов по проверочным хешам ключей.
/// </summary>
public interface IClientRepository
{
    Task<IReadOnlyList<OwnedClient>> FindByOwnerAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> TouchHeartbeatAsync(Guid clientId, CancellationToken cancellationToken = default);
    Task<bool> UpdateCapabilitiesAsync(Guid clientId, string os, string osVersion,
        string architecture, string defaultShell, string shellsJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт включённого клиента, принадлежащего указанному пользователю.
    /// </summary>
    /// <param name="userId">Идентификатор владельца клиента.</param>
    /// <param name="name">Имя клиента.</param>
    /// <param name="uppercaseSha256Hash">Верхнерегистровый SHA-256 хеш ключа.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Созданный клиент без секрета ключа.</returns>
    Task<OwnedClient> CreateAsync(Guid userId, string name, string uppercaseSha256Hash, CancellationToken cancellationToken = default);

    Task<OwnedClient?> RenameAsync(Guid userId, Guid clientId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Заменяет ключ принадлежащего пользователю клиента и включает его; повторная установка того же ключа активному клиенту не меняет аудит-время.
    /// </summary>
    /// <param name="userId">Идентификатор владельца клиента.</param>
    /// <param name="clientId">Идентификатор клиента.</param>
    /// <param name="uppercaseSha256Hash">Верхнерегистровый SHA-256 хеш нового ключа.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Обновлённый клиент либо <see langword="null"/>, если клиент не принадлежит пользователю.</returns>
    Task<OwnedClient?> ReplaceBearerKeyHashAsync(Guid userId, Guid clientId, string uppercaseSha256Hash, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Помечает принадлежащего пользователю клиента удалённым без удаления истории заданий.
    /// Bearer-ключ клиента сразу перестаёт действовать.
    /// </summary>
    Task<bool> DeleteAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Находит клиентов, хеш ключа которых совпадает с переданным SHA-256 хешем без учёта регистра.
    /// </summary>
    /// <param name="uppercaseSha256Hash">SHA-256 хеш в шестнадцатеричном виде, приведённый к верхнему регистру.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Список совпавших клиентов; пустой список, если совпадений нет.</returns>
    Task<IReadOnlyList<Client>> FindByBearerKeyHashCandidatesAsync(string uppercaseSha256Hash,
        CancellationToken cancellationToken = default);
}

public sealed record OwnedClient(Guid Id, string Name, bool IsEnabled, DateTime CreatedAt, DateTime UpdatedAt,
    string? OperatingSystem = null, string? OsVersion = null, string? Architecture = null,
    string? DefaultShell = null, string? ShellsJson = null, DateTime? CapabilitiesUpdatedAt = null,
    DateTime? LastSeenAt = null);
public sealed record OwnedJob(Guid Id, Guid ClientId, string Command, string? WorkingDirectory, int TimeoutSeconds,
    JobStatus Status, string? Stdout, string? Stderr, int? ExitCode, DateTime CreatedAt, DateTime UpdatedAt);

public interface IUserAccessValidator
{
    Task<bool> IsOAuthAllowedAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class UserAccessValidator(IUserRepository users) : IUserAccessValidator
{
    public async Task<bool> IsOAuthAllowedAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await users.FindByIdAsync(userId, cancellationToken))?.IsOAuthAllowed == true;
}

/// <summary>
/// Предоставляет операции обслуживания базы данных.
/// </summary>
public interface IDatabaseMaintenance
{
    /// <summary>
    /// Применяет ожидающие миграции базы данных.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Задача, завершающаяся после применения миграций.</returns>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет возможность подключения к базе данных.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns><see langword="true"/>, если подключение возможно; иначе <see langword="false"/>.</returns>
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}

public interface IOAuthScopeRepository
{
    Task EnsureCommandsResourceAsync(string resource, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class JobRepository(CommandLineMcpDbContext db) : IJobRepository
{
    private const int UnknownGraceSeconds = 120;

    public async Task<OwnedJob> CreateAsync(Guid clientId, string command, string? workingDirectory, int timeoutSeconds,
        CancellationToken cancellationToken = default, JobKind kind = JobKind.Command, string? shell = null)
    {
        var job = new Job
        {
            ClientId = clientId,
            Command = command,
            Kind = kind,
            Shell = shell,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            Status = JobStatus.Pending
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return ToOwnedJob(job);
    }

    public async Task<OwnedJob?> FindOwnedByIdAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.AsNoTracking().Include(x => x.Client)
            .SingleOrDefaultAsync(x => x.Id == jobId && x.Client.UserId == userId, cancellationToken);
        return job is null ? null : ToOwnedJob(job);
    }

    private static OwnedJob ToOwnedJob(Job j) => new(j.Id, j.ClientId, j.Command, j.WorkingDirectory,
        j.TimeoutSeconds, j.Status, j.Stdout, j.Stderr, j.ExitCode, j.CreatedAt, j.UpdatedAt);

    /// <inheritdoc />
    public Task<int> ExpireStaleRunningAsync(Guid? clientId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = db.Jobs.Where(j => j.Status == JobStatus.Running &&
            j.UpdatedAt.AddSeconds((double)j.TimeoutSeconds + UnknownGraceSeconds) <= now);
        if (clientId.HasValue) query = query.Where(j => j.ClientId == clientId.Value);
        return query.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Unknown)
            .SetProperty(j => j.UpdatedAt, now), cancellationToken);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

/// <inheritdoc />
public sealed class UserRepository(CommandLineMcpDbContext db) : IUserRepository
{
    public async Task<User> FindOrCreateByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default)
    {
        if (telegramId <= 0) throw new ArgumentOutOfRangeException(nameof(telegramId));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(x => x.TelegramId == telegramId, cancellationToken);
        if (user is null)
        {
            user = new User { TelegramId = telegramId, IsOAuthAllowed = false, RoleId = 0 };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
    /// <inheritdoc />
    public Task<User?> FindByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.TelegramId == telegramId, cancellationToken);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<UserBatchUpdateResult> DenyOAuthAsync(IReadOnlyCollection<long> telegramIds, CancellationToken cancellationToken = default) =>
        UpdateExistingAsync(telegramIds, user => user.IsOAuthAllowed = false, cancellationToken);

    /// <inheritdoc />
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
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IUserAccessValidator, UserAccessValidator>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IClientRepository, ClientRepository>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IDatabaseMaintenance, DatabaseMaintenance>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IOAuthScopeRepository, OAuthScopeRepository>(services);
        return services;
    }
}

/// <inheritdoc />
public sealed class OAuthScopeRepository(IOpenIddictScopeManager scopes) : IOAuthScopeRepository
{
    public async Task EnsureCommandsResourceAsync(string resource, CancellationToken cancellationToken = default)
    {
        var existing = await scopes.FindByNameAsync("commands", cancellationToken);
        if (existing is null)
        {
            var descriptor = new OpenIddictScopeDescriptor
            {
                Name = "commands",
                DisplayName = "CommandLineMCP command execution"
            };
            descriptor.Resources.Add(resource);
            await scopes.CreateAsync(descriptor, cancellationToken);
            return;
        }

        var current = await scopes.GetResourcesAsync(existing, cancellationToken);
        if (current.Contains(resource, StringComparer.Ordinal)) return;
        var updated = new OpenIddictScopeDescriptor();
        await scopes.PopulateAsync(updated, existing, cancellationToken);
        updated.Resources.Clear();
        updated.Resources.Add(resource);
        await scopes.UpdateAsync(existing, updated, cancellationToken);
    }
}

/// <inheritdoc />
public sealed class ClientRepository(CommandLineMcpDbContext db) : IClientRepository
{
    public async Task<IReadOnlyList<OwnedClient>> FindByOwnerAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.Clients.AsNoTracking().Where(x => x.UserId == userId && x.DeletedAt == null)
            .OrderBy(x => x.CreatedAt).Select(x => new OwnedClient(x.Id, x.Name, x.DeletedAt == null, x.CreatedAt, x.UpdatedAt,
                x.OperatingSystem, x.OsVersion, x.Architecture, x.DefaultShell, x.ShellsJson, x.CapabilitiesUpdatedAt, x.LastSeenAt))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<OwnedClient> CreateAsync(Guid userId, string name, string uppercaseSha256Hash, CancellationToken cancellationToken = default)
    {
        var client = new Client { UserId = userId, Name = name, BearerKeyHash = uppercaseSha256Hash };
        db.Clients.Add(client);
        await db.SaveChangesAsync(cancellationToken);
        return ToOwnedClient(client);
    }

    public Task<OwnedClient?> RenameAsync(Guid userId, Guid clientId, string name, CancellationToken cancellationToken = default) =>
        UpdateOwnedAsync(userId, clientId, client => client.Name = name, cancellationToken);

    /// <inheritdoc />
    public async Task<OwnedClient?> ReplaceBearerKeyHashAsync(Guid userId, Guid clientId, string uppercaseSha256Hash, CancellationToken cancellationToken = default)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId && x.UserId == userId && x.DeletedAt == null, cancellationToken);
        if (client is null)
        {
            return null;
        }
        if (string.Equals(client.BearerKeyHash, uppercaseSha256Hash, StringComparison.Ordinal))
        {
            return ToOwnedClient(client);
        }
        client.BearerKeyHash = uppercaseSha256Hash;
        await db.SaveChangesAsync(cancellationToken);
        return ToOwnedClient(client);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId && x.UserId == userId && x.DeletedAt == null, cancellationToken);
        if (client is null) return false;
        return await DeleteAsync(userId, clientId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        // Атомарно устанавливаем дату удаления, сохраняя задания и время аудита.
        var changed = await db.Clients.Where(c => c.Id == clientId && c.UserId == userId && c.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, DateTime.UtcNow), cancellationToken);
        return changed == 1;
    }

    private async Task<OwnedClient?> UpdateOwnedAsync(Guid userId, Guid clientId, Action<Client> update, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId && x.UserId == userId && x.DeletedAt == null, cancellationToken);
        if (client is null) return null;
        update(client);
        await db.SaveChangesAsync(cancellationToken);
        return ToOwnedClient(client);
    }

    private static OwnedClient ToOwnedClient(Client c) => new(c.Id, c.Name, c.IsEnabled, c.CreatedAt, c.UpdatedAt,
        c.OperatingSystem, c.OsVersion, c.Architecture, c.DefaultShell, c.ShellsJson, c.CapabilitiesUpdatedAt, c.LastSeenAt);

    public async Task<bool> TouchHeartbeatAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        // A heartbeat is operational presence, not an audit change.
        return await db.Clients.Where(c => c.Id == clientId && c.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenAt, DateTime.UtcNow), cancellationToken) == 1;
    }

    public async Task<bool> UpdateCapabilitiesAsync(Guid clientId, string os, string osVersion,
        string architecture, string defaultShell, string shellsJson,
        CancellationToken cancellationToken = default)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId && c.DeletedAt == null, cancellationToken);
        if (client is null) return false;
        client.OperatingSystem = os;
        client.OsVersion = osVersion;
        client.Architecture = architecture;
        client.DefaultShell = defaultShell;
        client.ShellsJson = shellsJson;
        client.CapabilitiesUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Client>> FindByBearerKeyHashCandidatesAsync(string uppercaseSha256Hash,
        CancellationToken cancellationToken = default) => await db.Clients.AsNoTracking()
        .Where(c => c.DeletedAt == null && c.BearerKeyHash.ToUpper() == uppercaseSha256Hash).ToListAsync(cancellationToken);
}

/// <inheritdoc />
public sealed class DatabaseMaintenance(CommandLineMcpDbContext db) : IDatabaseMaintenance
{
    /// <inheritdoc />
    public Task MigrateAsync(CancellationToken cancellationToken = default) => db.Database.MigrateAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) => db.Database.CanConnectAsync(cancellationToken);
}
