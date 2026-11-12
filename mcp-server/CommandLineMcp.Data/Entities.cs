namespace CommandLineMcp.Data;

/// <summary>Предоставляет даты создания и последнего изменения сущности.</summary>
public interface IAuditedEntity
{
    /// <summary>Возвращает или задаёт дату и время создания сущности в UTC.</summary>
    DateTime CreatedAt { get; set; }
    /// <summary>Возвращает или задаёт дату и время последнего изменения сущности в UTC.</summary>
    DateTime UpdatedAt { get; set; }
}

/// <inheritdoc />
public sealed class User : IAuditedEntity
{
    public Guid Id { get; set; }
    public long TelegramId { get; set; }
    public bool IsOAuthAllowed { get; set; }
    public int RoleId { get; set; }
    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }
    /// <inheritdoc />
    public DateTime UpdatedAt { get; set; }
    public ICollection<Client> Clients { get; } = new List<Client>();
}

/// <inheritdoc />
public sealed class Client : IAuditedEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = null!;
    public string BearerKeyHash { get; set; } = null!;
    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }
    /// <inheritdoc />
    public DateTime UpdatedAt { get; set; }
    public User User { get; set; } = null!;
    public ICollection<Job> Jobs { get; } = new List<Job>();
}

/// <inheritdoc />
public sealed class Job : IAuditedEntity
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Command { get; set; } = null!;
    public string? WorkingDirectory { get; set; }
    public int TimeoutSeconds { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public int? ExitCode { get; set; }
    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }
    /// <inheritdoc />
    public DateTime UpdatedAt { get; set; }
    public Client Client { get; set; } = null!;
}

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Unknown,
    Cancelled
}
