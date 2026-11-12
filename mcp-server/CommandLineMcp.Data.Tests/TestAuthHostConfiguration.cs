using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CommandLineMcp.Data.Tests;

internal static class TestAuthHostConfiguration
{
    private const string CertificatePassword = "temporary-test-host-certificate-password";
    private static readonly Lazy<string> Certificate = new(CreateCertificate);

    public static void Apply(IWebHostBuilder builder, string temporaryDirectory, string connectionString)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = connectionString,
            ["OAuth:Issuer"] = "https://localhost/",
            ["OAuth:Resource"] = "https://localhost/mcp",
            ["OAuth:SecretsFile"] = Path.Combine(temporaryDirectory, "unused-test-secrets.json"),
            ["OAuth:DataProtectionKeysPath"] = Path.Combine(temporaryDirectory, "keys"),
            ["OAuth:SigningCertificate:PfxBase64"] = Certificate.Value,
            ["OAuth:SigningCertificate:Password"] = CertificatePassword,
            ["Telegram:ClientId"] = "test-telegram-client",
            ["Telegram:ClientSecret"] = "test-telegram-client-secret",
            ["Mcp:CorsAllowedOrigins:0"] = "https://chatgpt.com"
        };
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        foreach (var (key, value) in settings)
            if (value is not null) builder.UseSetting(key, value);
    }

    private static string CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, CertificatePassword));
    }
}
