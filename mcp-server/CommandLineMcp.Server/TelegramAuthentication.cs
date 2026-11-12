using CommandLineMcp.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Globalization;
using System.Net;
using System.Security.Claims;

namespace CommandLineMcp.Server;

/// <summary>
/// Регистрирует вход пользователя через Telegram и локальную cookie-сессию сервера.
/// </summary>
public static class TelegramAuthentication
{
    /// <summary>
    /// Добавляет OIDC-вход через Telegram и cookie-аутентификацию для веб-страниц сервера.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <param name="configuration">Конфигурация приложения с параметрами Telegram.</param>
    /// <returns>Коллекция сервисов для дальнейшей настройки.</returns>
    public static IServiceCollection AddCommandLineMcpTelegramLogin(
        this IServiceCollection services, IConfiguration configuration)
    {
        var clientId = configuration["Telegram:ClientId"];
        var clientSecret = configuration["Telegram:ClientSecret"];
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = SessionScheme;
                    options.DefaultSignInScheme = SessionScheme;
                    options.DefaultChallengeScheme = TelegramScheme;
                })
                .AddCookie(SessionScheme, options =>
                {
                    options.Cookie.Name = "__Host-CommandLineMcp.Session";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.Path = "/";
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.LoginPath = "/login";
                })
                .AddOpenIdConnect(TelegramScheme, options =>
                {
                    options.Authority = "https://oauth.telegram.org";
                    options.ClientId = clientId;
                    options.ClientSecret = clientSecret;
                    options.CallbackPath = "/auth/telegram/callback";
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.UsePkce = true;
                    options.RequireHttpsMetadata = true;
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip
                            | DecompressionMethods.Deflate
                            | DecompressionMethods.Brotli
                    };
                    options.ProtocolValidator.RequireNonce = true;
                    options.GetClaimsFromUserInfoEndpoint = false;
                    options.SaveTokens = false;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.MapInboundClaims = false;
                    options.SignInScheme = SessionScheme;
                    options.TokenValidationParameters.NameClaimType = "name";
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = context =>
                        {
                            var issuer = configuration["OAuth:Issuer"] ?? "https://cmd.example.com/";
                            context.ProtocolMessage.RedirectUri = new Uri(new Uri(issuer), "auth/telegram/callback").AbsoluteUri;
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = async context =>
                        {
                            var principal = context.Principal;
                            var telegramIdText = principal?.FindFirst("id")?.Value;
                            if (!long.TryParse(telegramIdText, NumberStyles.None, CultureInfo.InvariantCulture,
                                    out var telegramId) || telegramId <= 0)
                            {
                                context.Fail("Telegram returned an invalid user identifier.");
                                return;
                            }

                            var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                            var user = await users.FindOrCreateByTelegramIdAsync(telegramId, context.HttpContext.RequestAborted);
                            if (user is null || !user.IsOAuthAllowed)
                            {
                                context.Fail("This Telegram account is not enabled for server access.");
                                return;
                            }

                            var identity = principal!.Identity as ClaimsIdentity;
                            identity!.RemoveClaim(identity.FindFirst("sub")!);
                            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")));
                            identity.AddClaim(new Claim("telegram_id", telegramId.ToString(CultureInfo.InvariantCulture)));
                        },
                        OnRemoteFailure = context =>
                        {
                            context.HandleResponse();
                            context.Response.Redirect("/login?error=telegram");
                            return Task.CompletedTask;
                        }
                    };
                });
        }

        return services;
    }

    /// <summary>
    /// Именует локальную cookie-схему пользовательской сессии.
    /// </summary>
    public const string SessionScheme = "CommandLineMcp.Session";

    /// <summary>
    /// Именует OIDC-схему Telegram.
    /// </summary>
    public const string TelegramScheme = "Telegram";
}
