using CommandLineMcp.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace CommandLineMcp.Server;

/// <summary>
/// Регистрирует страницы входа и управления собственными клиентами, а также экран согласия MCP OAuth.
/// </summary>
public static class WebEndpoints
{
    /// <summary>
    /// Добавляет antiforgery-защиту для изменяющих состояние веб-форм.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <returns>Коллекция сервисов приложения.</returns>
    public static IServiceCollection AddCommandLineMcpWeb(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-CommandLineMcp.AntiForgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.FormFieldName = "__RequestVerificationToken";
        });
        services.AddMcpServer().WithHttpTransport().WithTools<CommandTools>();
        return services;
    }

    /// <summary>
    /// Добавляет веб-маршруты входа, согласия и управления клиентами.
    /// </summary>
    /// <param name="endpoints">Маршрутизатор приложения.</param>
    /// <returns>Маршрутизатор приложения.</returns>
    public static IEndpointRouteBuilder MapCommandLineMcpWebEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/login", LoginAsync);
        endpoints.MapPost("/logout", LogoutAsync);
        endpoints.MapGet("/clients", ClientsPageAsync);
        endpoints.MapPost("/clients/create", CreateClientAsync);
        endpoints.MapPost("/clients/{clientId:guid}/rename", RenameClientAsync);
        endpoints.MapPost("/clients/{clientId:guid}/rotate", RotateClientKeyAsync);
        endpoints.MapPost("/clients/{clientId:guid}/revoke", RevokeClientAsync);
        endpoints.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync);
        endpoints.MapPost("/connect/token", TokenAsync);
        return endpoints;
    }

    private static IResult LoginAsync(HttpContext context)
    {
        ApplyPrivateHeaders(context.Response);
        if (context.Request.Query.ContainsKey("error"))
            return Html(context, "<h1>Вход не выполнен</h1><p>Убедитесь, что ваш Telegram ID разрешён для входа, и попробуйте снова.</p><a href='/login'>Повторить</a>");
        var returnUrl = context.Request.Query["returnUrl"].ToString();
        if (!IsLocalPath(returnUrl)) returnUrl = "/clients";
        return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl },
            [TelegramAuthentication.TelegramScheme]);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        ApplyPrivateHeaders(context.Response);
        await antiforgery.ValidateRequestAsync(context);
        await context.SignOutAsync(TelegramAuthentication.SessionScheme);
        return Results.Redirect("/login");
    }

    private static async Task<IResult> ClientsPageAsync(HttpContext context, IUserRepository users,
        IClientRepository clients, IAntiforgery antiforgery)
    {
        ApplyPrivateHeaders(context.Response);
        var user = await GetSessionUserAsync(context, users);
        if (user is null) return Results.Redirect("/login?returnUrl=%2Fclients");
        var owned = await clients.FindByOwnerAsync(user.Id, context.RequestAborted);
        var tokens = antiforgery.GetAndStoreTokens(context);
        var html = new StringBuilder("<!doctype html><html lang='ru'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Клиенты</title><h1>Клиенты</h1>");
        html.Append("<form method='post' action='/logout'>").Append(Token(tokens)).Append("<button>Выйти</button></form>");
        html.Append("<form method='post' action='/clients/create'>").Append(Token(tokens))
            .Append("<label>Имя клиента <input name='name' maxlength='100' required></label><button>Создать клиента</button></form>");
        if (owned.Count == 0) html.Append("<p>Клиентов пока нет.</p>");
        foreach (var client in owned)
        {
            html.Append("<section><h2>").Append(E(client.Name)).Append("</h2><p>ID: ").Append(client.Id)
                .Append(" · ").Append(client.IsEnabled ? "Активен" : "Ключ отозван").Append("</p>")
                .Append("<form method='post' action='/clients/").Append(client.Id).Append("/rename'>").Append(Token(tokens))
                .Append("<label>Новое имя <input name='name' maxlength='100' required></label><button>Переименовать</button></form>")
                .Append("<form method='post' action='/clients/").Append(client.Id).Append("/rotate'>").Append(Token(tokens))
                .Append("<button>Выпустить новый ключ</button></form>")
                .Append("<form method='post' action='/clients/").Append(client.Id).Append("/revoke'>").Append(Token(tokens))
                .Append("<button>Отозвать ключ</button></form></section>");
        }
        return Html(context, html.ToString());
    }

    private static async Task<IResult> CreateClientAsync(HttpContext context, IUserRepository users,
        IClientRepository clients, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context);
        var user = await GetSessionUserAsync(context, users);
        if (user is null) return Results.Unauthorized();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = NormalizeName(form["name"].ToString());
        if (name is null) return Results.BadRequest("Invalid client name.");
        var secret = NewBearerKey();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var client = await clients.CreateAsync(user.Id, name, hash, context.RequestAborted);
        return SecretPage(context, client.Id, secret);
    }

    private static async Task<IResult> RenameClientAsync(HttpContext context, Guid clientId,
        IUserRepository users, IClientRepository clients, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context);
        var user = await GetSessionUserAsync(context, users);
        if (user is null) return Results.Unauthorized();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = NormalizeName(form["name"].ToString());
        if (name is null) return Results.BadRequest("Invalid client name.");
        if (await clients.RenameAsync(user.Id, clientId, name, context.RequestAborted) is null)
            return Results.NotFound();
        return Results.Redirect("/clients");
    }

    private static async Task<IResult> RotateClientKeyAsync(HttpContext context, Guid clientId,
        IUserRepository users, IClientRepository clients, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context);
        var user = await GetSessionUserAsync(context, users);
        if (user is null) return Results.Unauthorized();
        var secret = NewBearerKey();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        if (await clients.ReplaceBearerKeyHashAsync(user.Id, clientId, hash, context.RequestAborted) is null)
            return Results.NotFound();
        return SecretPage(context, clientId, secret);
    }

    private static async Task<IResult> RevokeClientAsync(HttpContext context, Guid clientId,
        IUserRepository users, IClientRepository clients, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context);
        var user = await GetSessionUserAsync(context, users);
        if (user is null) return Results.Unauthorized();
        if (!await clients.RevokeAsync(user.Id, clientId, context.RequestAborted)) return Results.NotFound();
        return Results.Redirect("/clients");
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext context, IUserRepository users,
        IUserAccessValidator access, IConfiguration configuration, IAntiforgery antiforgery,
        IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations)
    {
        ApplyPrivateHeaders(context.Response);
        var request = context.GetOpenIddictServerRequest();
        if (request is null) return Results.BadRequest("Invalid authorization request.");
        var auth = await context.AuthenticateAsync(TelegramAuthentication.SessionScheme);
        var userIdText = auth.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdText, out var userId))
        {
            var returnUrl = context.Request.GetEncodedPathAndQuery();
            return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }
        if (!await access.IsOAuthAllowedAsync(userId, context.RequestAborted))
            return OAuthError(context, OpenIddictConstants.Errors.AccessDenied, "This account is not enabled for OAuth.");
        if (!request.GetScopes().Contains("commands", StringComparer.Ordinal))
            return OAuthError(context, OpenIddictConstants.Errors.InvalidScope, "The commands scope is required.");
        var resource = configuration["OAuth:Resource"] ?? "https://cmd.example.com/mcp";
        if (context.Request.Method == "GET")
        {
            var token = antiforgery.GetAndStoreTokens(context);
            var origin = ValidOrigin(request.RedirectUri);
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'" +
                (origin is null ? "" : " " + origin) + "; base-uri 'none'; frame-ancestors 'none'";
            var persistenceNotice = request.GetScopes().Contains(OpenIddictConstants.Scopes.OfflineAccess, StringComparer.Ordinal)
                ? " Также будет выдано обновляемое разрешение, действующее до 30 дней."
                : string.Empty;
            var html = new StringBuilder("<!doctype html><html lang='ru'><meta charset='utf-8'><title>Разрешение доступа</title><h1>Разрешить доступ к CommandLineMCP?</h1><p>Приложение получит доступ к инструментам выполнения команд от имени вашей учётной записи." + E(persistenceNotice) + "</p><form method='post' action='/connect/authorize'>")
                .Append(Token(token));
            foreach (var pair in context.Request.Query)
                foreach (var value in pair.Value)
                    html.Append("<input type='hidden' name='").Append(E(pair.Key)).Append("' value='").Append(E(value ?? "")).Append("'>");
            html.Append("<button name='decision' value='allow'>Разрешить</button><button name='decision' value='deny'>Отказать</button></form>");
            return Html(context, html.ToString(), preservePolicy: true);
        }

        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["decision"] != "allow")
            return OAuthError(context, OpenIddictConstants.Errors.AccessDenied, "The user denied access.");
        var user = await users.FindByIdAsync(userId, context.RequestAborted);
        if (user is null || !user.IsOAuthAllowed)
            return OAuthError(context, OpenIddictConstants.Errors.AccessDenied, "This account is not enabled for OAuth.");
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            OpenIddictConstants.Claims.Name, OpenIddictConstants.Claims.Role);
        identity.SetClaim(OpenIddictConstants.Claims.Subject, user.Id.ToString("D"));
        var application = await applications.FindByClientIdAsync(request.ClientId!, context.RequestAborted);
        if (application is null)
            return OAuthError(context, OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");
        var authorizationDescriptor = new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = await applications.GetIdAsync(application, context.RequestAborted),
            Subject = user.Id.ToString("D"),
            Status = OpenIddictConstants.Statuses.Valid,
            Type = OpenIddictConstants.AuthorizationTypes.Permanent
        };
        foreach (var scope in request.GetScopes()) authorizationDescriptor.Scopes.Add(scope);
        var authorization = await authorizations.CreateAsync(authorizationDescriptor, context.RequestAborted);
        identity.SetClaim(OpenIddictConstants.Claims.Private.AuthorizationId,
            await authorizations.GetIdAsync(authorization, context.RequestAborted));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources(resource);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> TokenAsync(HttpContext context, IUserAccessValidator access,
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications)
    {
        ApplyPrivateHeaders(context.Response);
        var request = context.GetOpenIddictServerRequest();
        if (request is null) return Results.BadRequest("Invalid token request.");
        var application = string.IsNullOrWhiteSpace(request.ClientId) ? null :
            await applications.FindByClientIdAsync(request.ClientId, context.RequestAborted);
        if (application is null)
            return OAuthError(context, OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");
        var settings = await applications.GetSettingsAsync(application, context.RequestAborted);
        settings.TryGetValue("token_endpoint_auth_method", out var expectedMethod);
        expectedMethod ??= "client_secret_basic";
        var basicAuthentication = context.Request.Headers.Authorization.ToString()
            .StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
        var postAuthentication = context.Request.HasFormContentType &&
            (await context.Request.ReadFormAsync(context.RequestAborted)).ContainsKey("client_secret");
        if ((expectedMethod == "client_secret_basic" && (!basicAuthentication || postAuthentication)) ||
            (expectedMethod == "client_secret_post" && (basicAuthentication || !postAuthentication)))
            return OAuthError(context, OpenIddictConstants.Errors.InvalidClient,
                "The client authentication method does not match its registration.");
        var auth = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal is null)
            return OAuthError(context, OpenIddictConstants.Errors.InvalidGrant, "The grant is invalid or expired.");
        var subject = auth.Principal.GetClaim(OpenIddictConstants.Claims.Subject);
        if (!Guid.TryParse(subject, out var userId) || !await access.IsOAuthAllowedAsync(userId, context.RequestAborted))
        {
            await RevokeAuthorizationAsync(auth.Principal, authorizations, context.RequestAborted);
            return OAuthError(context, OpenIddictConstants.Errors.InvalidGrant, "The account is not enabled for OAuth.");
        }
        return Results.SignIn(auth.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task RevokeAuthorizationAsync(ClaimsPrincipal principal,
        IOpenIddictAuthorizationManager authorizations, CancellationToken cancellationToken)
    {
        var authorizationId = principal.GetClaim(OpenIddictConstants.Claims.Private.AuthorizationId);
        if (string.IsNullOrEmpty(authorizationId)) return;
        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is not null)
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
    }

    private static IResult OAuthError(HttpContext context, string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static async Task<User?> GetSessionUserAsync(HttpContext context, IUserRepository users)
    {
        var auth = await context.AuthenticateAsync(TelegramAuthentication.SessionScheme);
        var value = auth.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var id) ? await users.FindByIdAsync(id, context.RequestAborted) : null;
    }

    private static IResult SecretPage(HttpContext context, Guid clientId, string secret)
    {
        ApplyPrivateHeaders(context.Response);
        var html = "<!doctype html><html lang='ru'><meta charset='utf-8'><title>Новый ключ</title><h1>Сохраните ключ клиента</h1><p>Он показывается только сейчас. Скопируйте его в локальную конфигурацию клиента.</p><pre>" + E(secret) + "</pre><p>ID клиента: " + clientId + "</p><a href='/clients'>Вернуться к клиентам</a>";
        return Html(context, html);
    }

    private static IResult Html(HttpContext context, string html, bool preservePolicy = false)
    {
        ApplyPrivateHeaders(context.Response);
        if (!preservePolicy || !context.Response.Headers.ContainsKey("Content-Security-Policy"))
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static void ApplyPrivateHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private static string Token(AntiforgeryTokenSet tokens) => "<input type='hidden' name='__RequestVerificationToken' value='" + E(tokens.RequestToken ?? "") + "'>";
    private static string NewBearerKey() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static string? NormalizeName(string value)
    {
        var name = value.Trim();
        return name.Length is > 0 and <= 100 ? name : null;
    }
    private static bool IsLocalPath(string path) => path.StartsWith('/') && !path.StartsWith("//") && !path.StartsWith("/\\");
    private static string? ValidOrigin(string? redirectUri) => Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" ? uri.GetLeftPart(UriPartial.Authority) : null;
    private static string E(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static class Base64UrlEncoder
    {
        public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Добавляет MCP endpoint с обязательной проверкой политики пользователя.
    /// </summary>
    /// <param name="endpoints">Маршрутизатор приложения.</param>
    /// <returns>Маршрутизатор приложения.</returns>
    public static IEndpointRouteBuilder MapCommandLineMcpTools(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp("/mcp").RequireAuthorization("mcp");
        return endpoints;
    }
}
