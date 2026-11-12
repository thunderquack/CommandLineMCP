using CommandLineMcp.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CommandLineMcp.Server;

public static class OAuthConfiguration
{
    public static X509Certificate2 LoadSigningCertificate(OAuthOptions options)
    {
        try
        {
            var bytes = Convert.FromBase64String(options.SigningCertificate.PfxBase64);
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, options.SigningCertificate.Password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            var now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey
                || certificate.NotAfter.ToUniversalTime() <= now
                || certificate.NotBefore.ToUniversalTime() > now)
                throw new InvalidOperationException("OAuth signing certificate must be currently valid and include a private key.");
            return certificate;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("OAuth signing certificate configuration is invalid.", exception);
        }
    }

    public static IServiceCollection AddCommandLineMcpOAuth(this IServiceCollection services,
        IConfiguration configuration, OAuthOptions options)
    {
        var certificate = LoadSigningCertificate(options);
        var keyPath = Path.GetFullPath(options.DataProtectionKeysPath);
        Directory.CreateDirectory(keyPath);

        services.AddSingleton(certificate);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath))
            .ProtectKeysWithCertificate(certificate);
        services.AddOpenIddict()
            .AddCore(builder => builder.UseEntityFrameworkCore().UseDbContext<CommandLineMcpDbContext>())
            .AddServer(builder =>
            {
                builder.SetIssuer(new Uri(options.Issuer, UriKind.Absolute));
                builder.RegisterResources(options.Resource);
                builder.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");
                builder.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange();
                builder.RegisterScopes("commands", OpenIddictConstants.Scopes.OfflineAccess);
                builder.SetAccessTokenLifetime(TimeSpan.FromMinutes(15))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(30))
                    .SetRefreshTokenReuseLeeway(TimeSpan.Zero)
                    .UseReferenceRefreshTokens();
                builder.AddSigningCertificate(certificate).AddEncryptionCertificate(certificate);
                var aspNetCore = builder.UseAspNetCore();
                if (options.AllowHttpTransport)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
                aspNetCore.EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                builder.AddEventHandler<OpenIddictServerEvents.ApplyConfigurationResponseContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        if (context.Error is null)
                        {
                            context.Response.SetParameter("issuer", options.Issuer);
                            context.Response.SetParameter("authorization_endpoint", options.Issuer.TrimEnd('/') + "/connect/authorize");
                            context.Response.SetParameter("token_endpoint", options.Issuer.TrimEnd('/') + "/connect/token");
                            context.Response.SetParameter("revocation_endpoint", options.Issuer.TrimEnd('/') + "/connect/revoke");
                            context.Response.SetParameter("registration_endpoint", options.Issuer.TrimEnd('/') + "/register");
                        }
                        return ValueTask.CompletedTask;
                    }));
                builder.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(handler =>
                    handler.UseScopedHandler<RefreshTokenReplayHandler>().SetOrder(int.MinValue + 1_000));
            })
            .AddValidation(builder =>
            {
                builder.SetIssuer(new Uri(options.Issuer, UriKind.Absolute));
                builder.AddAudiences(options.Resource);
                builder.EnableAuthorizationEntryValidation();
                builder.EnableTokenEntryValidation();
                builder.UseLocalServer();
                builder.UseAspNetCore();
            });

        services.Configure<OpenIddictServerOptions>(serverOptions =>
        {
            serverOptions.CodeChallengeMethods.Clear();
            serverOptions.CodeChallengeMethods.Add("S256");
        });

        services.AddSingleton<IValidateOptions<OAuthOptions>, OAuthOptionsValidator>();
        return services;
    }

    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        var trustAllProxies = configuration.GetValue<bool>("ForwardedHeaders:TrustAllProxies");
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = trustAllProxies || proxies.Length > 0 ?
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost :
                ForwardedHeaders.None;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            if (!trustAllProxies)
            {
                foreach (var value in proxies)
                {
                    if (System.Net.IPAddress.TryParse(value, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                    else
                    {
                        throw new InvalidOperationException("ForwardedHeaders:KnownProxies contains an invalid IP address.");
                    }
                }
            }
        });
        return services;
    }
}

public sealed class OAuthOptionsValidator : IValidateOptions<OAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, OAuthOptions options)
    {
        if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps || issuer.AbsolutePath != "/")
            return ValidateOptionsResult.Fail("OAuth:Issuer must be an HTTPS origin ending in '/'.");
        if (!Uri.TryCreate(options.Resource, UriKind.Absolute, out var resource) || resource.Scheme != Uri.UriSchemeHttps)
            return ValidateOptionsResult.Fail("OAuth:Resource must be an HTTPS URI.");
        if (!Path.IsPathFullyQualified(options.DataProtectionKeysPath))
            return ValidateOptionsResult.Fail("OAuth:DataProtectionKeysPath must be an absolute path.");
        if (string.IsNullOrWhiteSpace(options.SigningCertificate.PfxBase64) || string.IsNullOrWhiteSpace(options.SigningCertificate.Password))
            return ValidateOptionsResult.Fail("OAuth signing certificate PFX and password are required.");
        return ValidateOptionsResult.Success;
    }
}
