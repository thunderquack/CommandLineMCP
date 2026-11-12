using System.Diagnostics;
using System.Text.Json;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class AuthSecretsInitializerTests
{
    [TestMethod]
    public async Task InitSecretsCreatesProtectedCertificateAndRefusesOverwriteWithoutPrintingSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server.secrets.json");
        try
        {
            var first = await RunAdminAsync(path);
            Assert.AreEqual(0, first.ExitCode, first.Stderr);
            Assert.AreEqual($"Secrets file created: {path}{Environment.NewLine}", first.Stdout);
            Assert.IsFalse(first.Stdout.Contains("PfxBase64", StringComparison.Ordinal));
            Assert.IsFalse(first.Stdout.Contains("Password", StringComparison.Ordinal));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var signing = document.RootElement.GetProperty("OAuth").GetProperty("SigningCertificate");
            var pfxBase64 = signing.GetProperty("PfxBase64").GetString();
            var password = signing.GetProperty("Password").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(pfxBase64));
            Assert.IsFalse(string.IsNullOrWhiteSpace(password));
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(pfxBase64!), password,
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            Assert.IsTrue(certificate.HasPrivateKey);
            var original = await File.ReadAllBytesAsync(path);

            var second = await RunAdminAsync(path);
            Assert.AreEqual(3, second.ExitCode);
            Assert.AreEqual(string.Empty, second.Stdout);
            Assert.IsFalse(second.Stderr.Contains(password!, StringComparison.Ordinal));
            Assert.IsFalse(second.Stderr.Contains(pfxBase64!, StringComparison.Ordinal));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunAdminAsync(string path)
    {
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "CommandLineMcp.Admin", "CommandLineMcp.Admin.csproj")))
            repositoryRoot = repositoryRoot.Parent;
        Assert.IsNotNull(repositoryRoot, "Could not locate the Admin project from the test output directory.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(Path.Combine(repositoryRoot.FullName, "CommandLineMcp.Admin", "CommandLineMcp.Admin.csproj"));
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add("auth");
        process.StartInfo.ArgumentList.Add("init-secrets");
        process.StartInfo.ArgumentList.Add(path);
        Assert.IsTrue(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
