using System.ComponentModel.DataAnnotations;

namespace CommandLineMcp.Server;

public sealed class OAuthOptions
{
    public const string SectionName = "OAuth";

    [Required]
    public string Issuer { get; set; } = "https://cmd.example.com/";

    [Required]
    public string Resource { get; set; } = "https://cmd.example.com/mcp";

    public string SecretsFile { get; set; } = "/run/secrets/server.secrets.json";

    public string DataProtectionKeysPath { get; set; } = "/data/keys";

    /// <summary>Allows HTTP between a TLS-terminating proxy and the app. Never expose that HTTP listener publicly.</summary>
    public bool AllowHttpTransport { get; set; }

    public SigningCertificateOptions SigningCertificate { get; set; } = new();

    public sealed class SigningCertificateOptions
    {
        [Required]
        public string PfxBase64 { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
