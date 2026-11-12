using System.Globalization;
using CommandLineMcp.Data;
using Microsoft.EntityFrameworkCore;

public static class AdminCommandRunner
{
    public const string HelpText = """
        CommandLineMcp.Admin commands:
          users allow-oauth <TelegramId...>  Allow OAuth; create missing users with RoleId 0.
          users deny-oauth <TelegramId...>   Deny OAuth for existing users.
          users set-role <TelegramId> <0|1>  Set the role for an existing user.
          users show <TelegramId>            Show user ID, Telegram ID, OAuth, role and timestamps.
          --help                       Show this help.
        """;

    public static async Task<int> RunAsync(
        string[] args,
        CommandLineMcpDbContext db,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, stderr, out var command, out var ids, out var role)) return 2;

        try
        {
            await db.Database.MigrateAsync(cancellationToken);

            switch (command)
            {
                case "allow-oauth":
                    await ApplyOAuthAsync(db, ids, true, cancellationToken);
                    await stdout.WriteLineAsync($"OAuth allowed for {ids.Length} user(s).");
                    return 0;
                case "deny-oauth":
                    return await ApplyExistingAsync(db, ids, user => user.IsOAuthAllowed = false,
                        "OAuth denied", stdout, cancellationToken);
                case "set-role":
                    return await ApplyExistingAsync(db, ids, user => user.RoleId = role,
                        "Role updated", stdout, cancellationToken);
                case "show":
                {
                    var user = await db.Users.SingleOrDefaultAsync(x => x.TelegramId == ids[0], cancellationToken);
                    if (user is null)
                    {
                        await stderr.WriteLineAsync($"User with Telegram ID {ids[0]} was not found.");
                        return 3;
                    }
                    await stdout.WriteLineAsync(string.Join(" ",
                        $"Id={user.Id:D}", $"TelegramId={user.TelegramId}",
                        $"IsOAuthAllowed={user.IsOAuthAllowed}", $"RoleId={user.RoleId}",
                        $"CreatedAt={user.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
                        $"UpdatedAt={user.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}"));
                    return 0;
                }
                default:
                    return 2;
            }
        }
        catch (MissingUsersException ex)
        {
            await stderr.WriteLineAsync($"User(s) not found: {string.Join(", ", ex.TelegramIds)}.");
            return 3;
        }
        catch (Exception)
        {
            await stderr.WriteLineAsync("Database operation failed.");
            return 1;
        }
    }

    private static async Task ApplyOAuthAsync(CommandLineMcpDbContext db, long[] ids, bool allowed,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var users = await db.Users.Where(user => ids.Contains(user.TelegramId)).ToListAsync(cancellationToken);
        var known = users.Select(x => x.TelegramId).ToHashSet();
        var created = false;
        foreach (var id in ids.Where(id => !known.Contains(id)))
        {
            db.Users.Add(new User { TelegramId = id, IsOAuthAllowed = allowed, RoleId = 0 });
            created = true;
        }

        var changed = false;
        foreach (var user in users)
        {
            if (user.IsOAuthAllowed == allowed) continue;
            user.IsOAuthAllowed = allowed;
            changed = true;
        }
        if (changed || created) await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> ApplyExistingAsync(CommandLineMcpDbContext db, long[] ids,
        Action<User> apply, string success, TextWriter stdout, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var users = await db.Users.Where(user => ids.Contains(user.TelegramId)).ToListAsync(cancellationToken);
        var found = users.Select(x => x.TelegramId).ToHashSet();
        var missing = ids.Where(id => !found.Contains(id)).ToArray();
        if (missing.Length != 0) throw new MissingUsersException(missing);

        var changed = false;
        foreach (var user in users)
        {
            var oldRole = user.RoleId;
            var oldOAuth = user.IsOAuthAllowed;
            apply(user);
            changed |= user.RoleId != oldRole || user.IsOAuthAllowed != oldOAuth;
        }
        if (changed) await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await stdout.WriteLineAsync($"{success} for {ids.Length} user(s).");
        return 0;
    }

    private static bool TryParse(string[] args, TextWriter stderr, out string command, out long[] ids, out int role)
    {
        command = args.Length > 1 && args[0] == "users" ? args[1] : "";
        ids = [];
        role = 0;
        var commandArgs = args.Length > 1 && args[0] == "users" ? args.Skip(2).ToArray() : [];
        var validCount = command switch
        {
            "allow-oauth" or "deny-oauth" => commandArgs.Length >= 1,
            "set-role" => commandArgs.Length == 2,
            "show" => commandArgs.Length == 1,
            _ => false
        };
        if (!validCount)
        {
            stderr.WriteLine("Invalid arguments. Use --help for usage.");
            return false;
        }

        if (command == "set-role" && commandArgs[1] is not ("0" or "1"))
        {
            stderr.WriteLine("Role must be 0 or 1.");
            return false;
        }
        if (command == "set-role") role = commandArgs[1] == "1" ? 1 : 0;

        var inputIds = command == "set-role" ? commandArgs.Take(1) : commandArgs;
        var parsed = new List<long>();
        foreach (var value in inputIds)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                stderr.WriteLine("Telegram IDs must be positive integers.");
                return false;
            }
            if (!parsed.Contains(id)) parsed.Add(id);
        }
        ids = parsed.ToArray();
        return true;
    }

    private sealed class MissingUsersException(long[] telegramIds) : Exception
    {
        public long[] TelegramIds { get; } = telegramIds;
    }
}
