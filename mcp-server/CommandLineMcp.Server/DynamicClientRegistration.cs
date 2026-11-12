using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

namespace CommandLineMcp.Server;

public static class DynamicClientRegistration
{
    private const int MaximumRequestBytes = 16 * 1024;

    public static IServiceCollection AddCommandLineMcpDynamicRegistration(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy("dynamic-registration", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                })));
        return services;
    }

    public static IEndpointRouteBuilder MapDynamicClientRegistration(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/register", RegisterAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes))
            .RequireRateLimiting("dynamic-registration");
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(HttpContext context, IOpenIddictApplicationManager applications,
        IConfiguration configuration)
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaximumRequestBytes;
        if (context.Request.ContentLength is > MaximumRequestBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Registration request is too large.");

        RegistrationRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RegistrationRequest>(context.Request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 8 }, context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid_client_metadata" });
        }
        var error = "Invalid registration metadata.";
        if (request is null || !ValidMetadata(request, out var redirects, out error))
            return Results.BadRequest(new { error = "invalid_client_metadata", error_description = error });

        var clientId = Base64Url(RandomNumberGenerator.GetBytes(32));
        var clientSecret = Base64Url(RandomNumberGenerator.GetBytes(48));
        var authenticationMethod = request.TokenEndpointAuthMethod ?? "client_secret_basic";
        var grants = request.GrantTypes ?? ["authorization_code"];
        var responseTypes = request.ResponseTypes ?? ["code"];
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            DisplayName = string.IsNullOrWhiteSpace(request.ClientName) ? "MCP client" : request.ClientName.Trim(),
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.Endpoints.Revocation,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Prefixes.Scope + "commands"
            },
            Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
        };
        if (grants.Contains("refresh_token", StringComparer.Ordinal))
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
        if (request.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(OpenIddictConstants.Scopes.OfflineAccess, StringComparer.Ordinal) == true)
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess);
        descriptor.AddResourcePermissions(configuration["OAuth:Resource"] ?? "https://cmd.example.com/mcp");
        foreach (var redirect in redirects) descriptor.RedirectUris.Add(new Uri(redirect));
        descriptor.Settings["token_endpoint_auth_method"] = authenticationMethod;
        await applications.CreateAsync(descriptor, context.RequestAborted);

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Json(new
        {
            client_id = clientId,
            client_secret = clientSecret,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            client_secret_expires_at = 0,
            redirect_uris = redirects,
            grant_types = grants,
            response_types = responseTypes,
            token_endpoint_auth_method = authenticationMethod,
            client_name = descriptor.DisplayName
        }, statusCode: StatusCodes.Status201Created);
    }

    private static bool ValidMetadata(RegistrationRequest request, out string[] redirects, out string error)
    {
        redirects = [];
        error = "Unsupported client metadata.";
        if (request.RedirectUris is null || request.RedirectUris.Length is < 1 or > 10 ||
            request.RedirectUris.Any(uri => uri is null || uri.Length > 2048 || !ValidRedirect(uri)))
        {
            error = "redirect_uris must contain one to ten exact HTTPS URIs.";
            return false;
        }
        if (request.ClientName?.Length > 100)
        {
            error = "client_name must not exceed 100 characters.";
            return false;
        }
        if (request.TokenEndpointAuthMethod is not null and not ("client_secret_basic" or "client_secret_post"))
        {
            error = "token_endpoint_auth_method must be client_secret_basic or client_secret_post.";
            return false;
        }
        if (request.GrantTypes is not null &&
            (!request.GrantTypes.Contains("authorization_code", StringComparer.Ordinal) ||
             request.GrantTypes.Any(grant => grant is not ("authorization_code" or "refresh_token"))))
        {
            error = "Only authorization_code and refresh_token are supported.";
            return false;
        }
        if (request.ResponseTypes is not null &&
            (request.ResponseTypes.Length != 1 || request.ResponseTypes[0] != "code"))
        {
            error = "Only the code response type is supported.";
            return false;
        }
        if (request.Scope is not null && request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(scope => scope is not ("commands" or OpenIddictConstants.Scopes.OfflineAccess)))
        {
            error = "Only the commands and offline_access scopes are supported.";
            return false;
        }
        redirects = request.RedirectUris.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool ValidRedirect(string value)
    {
        if (value.Contains('*') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record RegistrationRequest(
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("grant_types")] string[]? GrantTypes,
        [property: JsonPropertyName("response_types")] string[]? ResponseTypes,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
        [property: JsonPropertyName("scope")] string? Scope);
}
