using CommandLineMcp.Data;
using CommandLineMcp.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var secretsFile = builder.Configuration["OAuth:SecretsFile"] ?? "/run/secrets/server.secrets.json";
        if (File.Exists(secretsFile))
        {
            builder.Configuration.AddJsonFile(secretsFile, optional: false, reloadOnChange: false);
            builder.Configuration.AddEnvironmentVariables();
        }
        else if (string.IsNullOrWhiteSpace(builder.Configuration["OAuth:SigningCertificate:PfxBase64"]) 
                || string.IsNullOrWhiteSpace(builder.Configuration["OAuth:SigningCertificate:Password"]))
        {
            throw new InvalidOperationException($"Required OAuth secrets file '{secretsFile}' is missing and no complete signing certificate was configured.");
        }

        builder.Configuration["OAuth:SecretsFile"] ??= secretsFile;
        builder.Configuration["OAuth:Issuer"] ??= "https://cmd.example.com/";
        builder.Configuration["OAuth:Resource"] ??= "https://cmd.example.com/mcp";
        builder.Configuration["OAuth:DataProtectionKeysPath"] ??= "/data/keys";
        if (string.IsNullOrWhiteSpace(builder.Configuration["Telegram:ClientId"]) ||
            string.IsNullOrWhiteSpace(builder.Configuration["Telegram:ClientSecret"]))
        {
            throw new InvalidOperationException("Telegram OIDC ClientId and ClientSecret are required.");
        }
        var oauthOptions = builder.Configuration.GetSection(OAuthOptions.SectionName).Get<OAuthOptions>()
            ?? throw new InvalidOperationException("OAuth configuration is required.");
        var resourceMetadata = new Uri(new Uri(oauthOptions.Issuer, UriKind.Absolute), ".well-known/oauth-protected-resource/mcp").AbsoluteUri;

        var connectionString = builder.Configuration.GetConnectionString("Database") ?? "Data Source=data/commandline-mcp.db";
        var databasePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        var databaseDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory)) Directory.CreateDirectory(databaseDirectory);

        builder.Services.AddDbContext<CommandLineMcpDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddCommandLineMcpDataRepositories();
        builder.Services.AddOptions<OAuthOptions>().Bind(builder.Configuration.GetSection(OAuthOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddCommandLineMcpOAuth(builder.Configuration, oauthOptions);
        builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
        builder.Services.AddCommandLineMcpTelegramLogin(builder.Configuration);
        builder.Services.AddCommandLineMcpWeb();
        builder.Services.AddCommandLineMcpDynamicRegistration();
        builder.Services.AddAuthorization(options => options.AddPolicy("mcp", policy =>
            policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.GetScopes().Contains("commands", StringComparer.Ordinal))));
        builder.Services.AddHostedService<UnknownJobWorker>();

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseMiddleware<OAuthCorsMiddleware>();
        app.UseHttpsRedirection();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") &&
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                if (!result.Succeeded || result.Principal is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceMetadata}\"";
                    return;
                }

                var subject = result.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? result.Principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                if (!Guid.TryParse(subject, out var userId) ||
                    !await context.RequestServices.GetRequiredService<IUserAccessValidator>()
                        .IsOAuthAllowedAsync(userId, context.RequestAborted))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.User = result.Principal;
            }
            await next();
            if (context.Request.Path.StartsWithSegments("/mcp")
                && context.Response.StatusCode == StatusCodes.Status401Unauthorized
                && !context.Response.Headers.ContainsKey("WWW-Authenticate"))
                context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceMetadata}\"";
        });
        app.UseAuthorization();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseMaintenance>();
            await database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IOAuthScopeRepository>()
                .EnsureCommandsResourceAsync(oauthOptions.Resource);
        }

        app.MapGet("/health", async (IDatabaseMaintenance database, CancellationToken cancellationToken) =>
        {
            try
            {
                return await database.CanConnectAsync(cancellationToken)
                    ? Results.Ok(new { status = "healthy", database = "reachable" })
                    : Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch
            {
                return Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new
        {
            resource = oauthOptions.Resource,
            authorization_servers = new[] { oauthOptions.Issuer },
            scopes_supported = new[]
            {
                "commands",
                OpenIddictConstants.Scopes.OfflineAccess,
            },
            bearer_methods_supported = new[] { "header" },
        }));

        app.MapClientJobEndpoints();
        app.MapCommandLineMcpWebEndpoints();
        app.MapDynamicClientRegistration();
        app.MapCommandLineMcpTools();

        await app.RunAsync();
    }
}
