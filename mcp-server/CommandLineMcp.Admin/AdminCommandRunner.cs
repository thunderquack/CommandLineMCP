using CommandLineMcp.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public static class AdminCommandRunner
{
    public const string HelpText = """
        CommandLineMcp.Admin commands:
          auth init-secrets [path]           Create OAuth signing secrets; refuses overwrite.
          users allow-oauth <TelegramId...>  Allow OAuth; create missing users with RoleId 0.
          users deny-oauth <TelegramId...>   Deny OAuth for existing users.
          users set-role <TelegramId> <0|1>  Set the role for an existing user.
          users show <TelegramId>            Show user ID, Telegram ID, OAuth, role and timestamps.
          clients create <TelegramId> <Name>  Create an enabled client and print its key once.
          clients set-key <TelegramId> <ClientId> [--stdin]  Rotate a client's key.
          clients delete <TelegramId> <ClientId>  Soft-delete an owned client (preserve jobs).
          --help                       Show this help.
        """;

    public static async Task<int> RunAsync(
        string[] args,
        IUserRepository users,
        IDatabaseMaintenance database,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default) =>
        await RunAsync(args, users, null, database, stdout, stderr, cancellationToken: cancellationToken);

    public static async Task<int> RunAsync(
        string[] args,
        IUserRepository users,
        IClientRepository? clients,
        IDatabaseMaintenance database,
        TextWriter stdout,
        TextWriter stderr,
        TextReader? stdin = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length > 0 && args[0] == "clients")
        {
            if (clients is null)
            {
                await stderr.WriteLineAsync("Database operation failed.");
                return 1;
            }
            return await RunClientsAsync(args, users, clients, database, stdout, stderr, stdin ?? Console.In, cancellationToken);
        }
        if (!TryParse(args, stderr, out var command, out var ids, out var role)) return 2;

        try
        {
            await database.MigrateAsync(cancellationToken);

            switch (command)
            {
                case "allow-oauth":
                    await users.AllowOAuthAsync(ids, cancellationToken);
                    await stdout.WriteLineAsync($"OAuth allowed for {ids.Length} user(s).");
                    return 0;
                case "deny-oauth":
                    return await ApplyExistingAsync(await users.DenyOAuthAsync(ids, cancellationToken), ids,
                        "OAuth denied", stdout, stderr);
                case "set-role":
                    return await ApplyExistingAsync(await users.SetRoleAsync(ids, role, cancellationToken), ids,
                        "Role updated", stdout, stderr);
                case "show":
                    {
                        var user = await users.FindByTelegramIdAsync(ids[0], cancellationToken);
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
        catch (Exception)
        {
            await stderr.WriteLineAsync("Database operation failed.");
            return 1;
        }
    }

    private static async Task<int> RunClientsAsync(string[] args, IUserRepository users, IClientRepository clients,
        IDatabaseMaintenance database, TextWriter stdout, TextWriter stderr, TextReader stdin, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || args[1] is not ("create" or "set-key" or "delete"))
        {
            await stderr.WriteLineAsync("Invalid arguments. Use --help for usage.");
            return 2;
        }

        var isCreate = args[1] == "create";
        var isDelete = args[1] == "delete";
        var valid = isCreate || isDelete
            ? args.Length == 4
            : args.Length == 4 || (args.Length == 5 && args[4] == "--stdin");
        if (!valid || !long.TryParse(args.ElementAtOrDefault(2), NumberStyles.None,
                CultureInfo.InvariantCulture, out var telegramId) || telegramId <= 0 ||
            (isCreate && string.IsNullOrWhiteSpace(args.ElementAtOrDefault(3))) ||
            (!isCreate && !Guid.TryParse(args.ElementAtOrDefault(3), out _)))
        {
            await stderr.WriteLineAsync("Invalid arguments. Use --help for usage.");
            return 2;
        }

        string? key = null;
        if (!isDelete && !isCreate && args.Length == 5)
        {
            var inputKey = await stdin.ReadLineAsync(cancellationToken);
            if (!IsValidBearerKey(inputKey))
            {
                await stderr.WriteLineAsync("Invalid key input.");
                return 2;
            }
            key = inputKey!;
        }
        else if (!isDelete)
        {
            key = GenerateBearerKey();
        }

        try
        {
            await database.MigrateAsync(cancellationToken);
            var user = await users.FindByTelegramIdAsync(telegramId, cancellationToken);
            if (user is null)
            {
                await stderr.WriteLineAsync("User was not found.");
                return 3;
            }

            if (isDelete)
            {
                var clientId = Guid.Parse(args[3]);
                if (!await clients.DeleteAsync(user.Id, clientId, cancellationToken))
                {
                    await stderr.WriteLineAsync("Client was not found.");
                    return 3;
                }

                await stdout.WriteLineAsync($"ClientId={clientId:D} Deleted=true");
                return 0;
            }

            var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key!)));
            if (isCreate)
            {
                var client = await clients.CreateAsync(user.Id, args[3], keyHash, cancellationToken);
                await stdout.WriteLineAsync($"ClientId={client.Id:D} Name={client.Name} BearerKey={key}");
                return 0;
            }

            var updated = await clients.ReplaceBearerKeyHashAsync(user.Id, Guid.Parse(args[3]), keyHash, cancellationToken);
            if (updated is null)
            {
                await stderr.WriteLineAsync("Client was not found.");
                return 3;
            }
            if (args.Length == 5)
            {
                await stdout.WriteLineAsync($"ClientId={updated.Id:D} KeyUpdated=true");
            }
            else
            {
                await stdout.WriteLineAsync($"ClientId={updated.Id:D} BearerKey={key}");
            }
            return 0;
        }
        catch (Exception)
        {
            await stderr.WriteLineAsync("Database operation failed.");
            return 1;
        }
    }

    private static bool IsValidBearerKey(string? key) => key is { Length: >= 32 and <= 4096 } &&
        !key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static string GenerateBearerKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');


    private static async Task<int> ApplyExistingAsync(UserBatchUpdateResult result, long[] ids,
        string success, TextWriter stdout, TextWriter stderr)
    {
        if (!result.Succeeded)
        {
            await stderr.WriteLineAsync($"User(s) not found: {string.Join(", ", result.MissingTelegramIds)}.");
            return 3;
        }
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

}
