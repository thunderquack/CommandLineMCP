namespace CommandLineMcp.Server;

/// <summary>Provides non-credentialed CORS for the MCP resource and OAuth token endpoints.</summary>
public sealed class OAuthCorsMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Content-Type", "MCP-Protocol-Version"
    };

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        var path = context.Request.Path;
        var isMcp = path.StartsWithSegments("/mcp");
        var isOAuthEndpoint = path.StartsWithSegments("/connect/token") || path.StartsWithSegments("/connect/revoke");
        string[]? allowedMethods = isMcp ? ["GET", "POST", "DELETE"] : isOAuthEndpoint ? ["POST"] : null;
        var origin = context.Request.Headers.Origin;
        if (allowedMethods is null || string.IsNullOrWhiteSpace(origin))
        {
            await next(context);
            return;
        }

        var allowedOrigin = "*";
        if (isMcp)
        {
            var allowedOrigins = configuration.GetSection("Mcp:CorsAllowedOrigins").Get<string[]>() ?? [];
            if (!allowedOrigins.Contains(origin.ToString(), StringComparer.Ordinal) || !IsExactHttpsOrigin(origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            allowedOrigin = origin.ToString();
        }

        var requestedMethod = context.Request.Headers["Access-Control-Request-Method"].ToString();
        if (HttpMethods.IsOptions(context.Request.Method) && !string.IsNullOrWhiteSpace(requestedMethod))
        {
            var requestedHeaders = context.Request.Headers["Access-Control-Request-Headers"].ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!allowedMethods.Contains(requestedMethod, StringComparer.OrdinalIgnoreCase) ||
                requestedHeaders.Any(header => !AllowedHeaders.Contains(header)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
            context.Response.Headers["Access-Control-Allow-Methods"] = string.Join(", ", allowedMethods);
            context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, MCP-Protocol-Version";
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
            context.Response.Headers["Access-Control-Allow-Methods"] = string.Join(", ", allowedMethods);
            context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, MCP-Protocol-Version";
            context.Response.Headers.Remove("Access-Control-Allow-Credentials");
            if (isMcp) context.Response.Headers.AppendCommaSeparatedValues("Vary", "Origin");
            return Task.CompletedTask;
        });
        await next(context);
    }

    private static bool IsExactHttpsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == "/" && string.Equals(uri.GetLeftPart(UriPartial.Authority), value, StringComparison.Ordinal);
}
