using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class WebSecurityHeadersTests
{
    private static readonly string[] RequiredHeaders = ["Authorization", "Content-Type", "MCP-Protocol-Version"];

    [TestMethod]
    public async Task TokenAndRevocationPreflightsAllowWildcardWithoutCredentials()
    {
        using var host = new TestHostFixture();
        using var client = host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        foreach (var path in new[] { "/connect/token", "/connect/revoke" })
        {
            using var response = await SendPreflightAsync(client, path, "https://attacker.example", "POST", RequiredHeaders);
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, $"{path} rejected the OAuth preflight.");
            AssertHeader(response, "Access-Control-Allow-Origin", "*");
            AssertHeader(response, "Access-Control-Allow-Methods", "POST");
            AssertAllowedHeaders(response);
            Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Credentials"),
                "OAuth CORS must not enable browser cookies.");
        }
    }

    [TestMethod]
    public async Task McpPreflightAllowsOnlyConfiguredHttpsOrigin()
    {
        using var host = new TestHostFixture();
        using var client = host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using (var allowed = await SendPreflightAsync(client, "/mcp", "https://chatgpt.com", "POST", RequiredHeaders))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.OK, allowed.StatusCode);
            AssertHeader(allowed, "Access-Control-Allow-Origin", "https://chatgpt.com");
            AssertAllowedHeaders(allowed);
            Assert.IsFalse(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        }

        using (var denied = await SendPreflightAsync(client, "/mcp", "https://attacker.example", "POST", RequiredHeaders))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.IsFalse(denied.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    [TestMethod]
    public async Task OAuthPreflightRejectsUnlistedMethodsAndHeaders()
    {
        using var host = new TestHostFixture();
        using var client = host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using (var method = await SendPreflightAsync(client, "/connect/token", "https://attacker.example", "PUT", RequiredHeaders))
            Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, method.StatusCode);
        using (var header = await SendPreflightAsync(client, "/connect/revoke", "https://attacker.example", "POST", [.. RequiredHeaders, "X-Not-Allowed"]))
            Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, header.StatusCode);
    }

    private sealed class TestHostFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CommandLineMcp.Tests", Guid.NewGuid().ToString("N"));
        public WebApplicationFactory<Program> Factory { get; }

        public TestHostFixture()
        {
            Directory.CreateDirectory(_directory);
            var connectionString = "Data Source=" + Path.Combine(_directory, "oauth.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                TestAuthHostConfiguration.Apply(builder, _directory, connectionString));
        }

        public void Dispose()
        {
            Factory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> SendPreflightAsync(HttpClient client, string path,
        string origin, string method, IReadOnlyCollection<string> headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", string.Join(',', headers));
        return await client.SendAsync(request);
    }

    private static void AssertHeader(HttpResponseMessage response, string name, string expected)
    {
        Assert.IsTrue(response.Headers.TryGetValues(name, out var values), $"Missing {name} header.");
        CollectionAssert.Contains(values!.ToArray(), expected);
    }

    private static void AssertAllowedHeaders(HttpResponseMessage response)
    {
        Assert.IsTrue(response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values));
        var actual = values!.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        foreach (var header in RequiredHeaders)
            Assert.IsTrue(actual.Contains(header, StringComparer.OrdinalIgnoreCase), $"Missing allowed CORS header {header}.");
    }
}
