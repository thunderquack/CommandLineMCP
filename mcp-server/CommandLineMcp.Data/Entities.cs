namespace CommandLineMcp.Data;

/// <summary>
/// Предоставляет даты создания и последнего изменения сущности.
/// </summary>
public interface IAuditedEntity
{
    /// <summary>
    /// Возвращает или задаёт дату и время создания сущности в UTC.
    /// </summary>
    DateTime CreatedAt { get; set; }

    /// <summary>
    /// Возвращает или задаёт дату и время последнего изменения сущности в UTC.
    /// </summary>
    DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Пользователь сервера, идентифицируемый по Telegram и владеющий клиентами.
/// </summary>
public sealed class User : IAuditedEntity
{
    /// <summary>
    /// Возвращает или задаёт уникальный идентификатор пользователя сервера.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Возвращает или задаёт уникальный Telegram ID пользователя.
    /// </summary>
    public long TelegramId { get; set; }

    /// <summary>
    /// Возвращает или задаёт признак, разрешён ли пользователю вход через OAuth.
    /// </summary>
    public bool IsOAuthAllowed { get; set; }

    /// <summary>
    /// Возвращает или задаёт числовой идентификатор роли: 0 — обычный пользователь, 1 — администратор.
    /// </summary>
    public int RoleId { get; set; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Возвращает принадлежащих пользователю клиентов.
    /// </summary>
    public ICollection<Client> Clients { get; } = new List<Client>();
}

/// <summary>
/// Клиент пользователя, который получает задания и отправляет результаты.
/// </summary>
public sealed class Client : IAuditedEntity
{
    /// <summary>
    /// Возвращает или задаёт уникальный идентификатор клиента.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Возвращает или задаёт идентификатор владельца клиента.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Возвращает или задаёт отображаемое имя клиента.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Возвращает или задаёт проверочный хеш Bearer-ключа; исходный секрет здесь не хранится.
    /// </summary>
    public string BearerKeyHash { get; set; } = null!;

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Возвращает пользователя, которому принадлежит клиент.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Возвращает задания, назначенные этому клиенту.
    /// </summary>
    public ICollection<Job> Jobs { get; } = new List<Job>();
}

/// <summary>
/// Команда, поставленная пользователем в очередь для выполнения конкретным клиентом, и сохранённый результат её выполнения.
/// </summary>
public sealed class Job : IAuditedEntity
{
    /// <summary>
    /// Возвращает или задаёт уникальный идентификатор задания.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Возвращает или задаёт идентификатор клиента, которому назначено задание.
    /// </summary>
    public Guid ClientId { get; set; }

    /// <summary>
    /// Возвращает или задаёт команду командной строки для выполнения.
    /// </summary>
    public string Command { get; set; } = null!;

    /// <summary>
    /// Возвращает или задаёт рабочую директорию; значение <see langword="null"/> означает, что она не задана.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Возвращает или задаёт таймаут выполнения в секундах.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Возвращает или задаёт текущее состояние задания.
    /// </summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// Возвращает или задаёт сохранённый стандартный вывод команды, если он получен.
    /// </summary>
    public string? Stdout { get; set; }

    /// <summary>
    /// Возвращает или задаёт сохранённый вывод ошибок команды, если он получен.
    /// </summary>
    public string? Stderr { get; set; }

    /// <summary>
    /// Возвращает или задаёт код завершения команды, если он получен.
    /// </summary>
    public int? ExitCode { get; set; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Возвращает клиента, которому назначено задание.
    /// </summary>
    public Client Client { get; set; } = null!;
}

/// <summary>
/// Состояние задания на стороне сервера.
/// </summary>
public enum JobStatus
{
    /// <summary>
    /// Задание создано и ожидает получения клиентом.
    /// </summary>
    Pending,

    /// <summary>
    /// Задание выдано клиенту для выполнения; подтверждённого результата ещё нет.
    /// </summary>
    Running,

    /// <summary>
    /// Выполнение завершено с кодом выхода 0.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Выполнение завершено с ошибкой или ненулевым кодом выхода.
    /// </summary>
    Failed,

    /// <summary>
    /// Выполнение прервано по истечении таймаута.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Исход выполнения неизвестен, например если клиент пропал во время выполнения; задание нельзя запускать автоматически повторно.
    /// </summary>
    Unknown,

    /// <summary>
    /// Задание отменено.
    /// </summary>
    Cancelled
}
