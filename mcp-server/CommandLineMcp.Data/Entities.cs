namespace CommandLineMcp.Data;

public interface IAuditedEntity
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

public sealed class User : IAuditedEntity
{
    public Guid Id { get; set; }
    public long TelegramId { get; set; }
    public bool IsOAuthAllowed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<Client> Clients { get; } = new List<Client>();
}

public sealed class Client : IAuditedEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = null!;
    public string BearerKeyHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public User User { get; set; } = null!;
    public ICollection<Job> Jobs { get; } = new List<Job>();
}

public sealed class Job : IAuditedEntity
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public int TimeoutSeconds { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public int? ExitCode { get; set; }
    public DateTime CreatedAt { get; set; }
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
