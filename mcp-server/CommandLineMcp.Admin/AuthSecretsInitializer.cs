using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

internal static class AuthSecretsInitializer
{
    private const string DefaultPath = "secrets/server.secrets.json";
    private const string Issuer = "https://cmd.example.com/";
    private const string Resource = "https://cmd.example.com/mcp";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length is < 2 or > 3 || args[1] != "init-secrets")
        {
            await stderr.WriteLineAsync("Usage: auth init-secrets [path]");
            return 2;
        }

        var target = Path.GetFullPath(args.Length == 3 ? args[2] : DefaultPath);
        if (File.Exists(target))
        {
            await stderr.WriteLineAsync("Secrets file already exists; refusing to overwrite it.");
            return 3;
        }

        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=cmd.example.com", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(10));

            var passwordBytes = RandomNumberGenerator.GetBytes(48);
            var password = Convert.ToBase64String(passwordBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            var pfx = certificate.Export(X509ContentType.Pfx, password);
            try
            {
                var config = new
                {
                    OAuth = new
                    {
                        Issuer,
                        Resource,
                        SecretsFile = "/run/secrets/server.secrets.json",
                        DataProtectionKeysPath = "/data/keys",
                        SigningCertificate = new
                        {
                            PfxBase64 = Convert.ToBase64String(pfx),
                            Password = password
                        }
                    },
                    Telegram = new { ClientId = "", ClientSecret = "" }
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                // CreateNew provides an atomic no-overwrite guard when another process races this command.
                await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteLineAsync(json);
                await writer.FlushAsync();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }

            await stdout.WriteLineAsync($"Secrets file created: {target}");
            return 0;
        }
        catch (IOException) when (File.Exists(target))
        {
            await stderr.WriteLineAsync("Secrets file already exists; refusing to overwrite it.");
            return 3;
        }
        catch (Exception)
        {
            await stderr.WriteLineAsync("Could not create secrets file.");
            return 1;
        }
    }
}
