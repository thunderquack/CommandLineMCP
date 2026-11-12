using CommandLineMcp.Server;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class TelegramOidcIntegrationTests
{
    [TestMethod]
    public async Task TelegramBackchannelDecompressesGzipJsonOverHttp()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ClientId"] = "test-client",
                ["Telegram:ClientSecret"] = "test-secret"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommandLineMcpTelegramLogin(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(TelegramAuthentication.TelegramScheme);

        Assert.IsInstanceOfType<HttpClientHandler>(options.BackchannelHttpHandler);
        var handler = (HttpClientHandler)options.BackchannelHttpHandler!;
        Assert.AreEqual(
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            handler.AutomaticDecompression);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var responseTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var request = new List<byte>();
            var buffer = new byte[1024];
            while (!Encoding.ASCII.GetString(request.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(buffer);
                request.AddRange(buffer.AsSpan(0, count).ToArray());
            }

            var body = Encoding.UTF8.GetBytes("{\"keys\":[]}");
            await using var compressed = new MemoryStream();
            await using (var gzip = new System.IO.Compression.GZipStream(compressed,
                             System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            {
                await gzip.WriteAsync(body);
            }

            var compressedBody = compressed.ToArray();
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: {compressedBody.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(compressedBody);
        });

        using var http = new HttpClient(handler, disposeHandler: false);
        var jsonTask = http.GetStringAsync($"http://127.0.0.1:{address.Port}/jwks");
        await responseTask;
        using var document = JsonDocument.Parse(await jsonTask);
        Assert.IsTrue(document.RootElement.TryGetProperty("keys", out var keys));
        Assert.AreEqual(JsonValueKind.Array, keys.ValueKind);
    }

    [TestMethod]
    public async Task LoginUsesOidcStateNoncePkceAndMapsAllowedTelegramUserToSession()
    {
        using var fixture = new AuthHostFixture();
        var userId = Guid.NewGuid();
        await fixture.SeedUserAsync(new User { Id = userId, TelegramId = 123456789, IsOAuthAllowed = true });

        using var http = fixture.CreateClient();
        var login = await http.GetAsync("/login");
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode, await login.Content.ReadAsStringAsync());
        var authorize = login.Headers.Location!;
        Assert.AreEqual("https://oauth.telegram.org/auth", authorize.GetLeftPart(UriPartial.Path));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        Assert.IsTrue(query.TryGetValue("state", out var state) && !string.IsNullOrWhiteSpace(state));
        Assert.IsTrue(query.TryGetValue("nonce", out var nonce) && !string.IsNullOrWhiteSpace(nonce));
        Assert.IsTrue(query.TryGetValue("code_challenge", out var challenge) && !string.IsNullOrWhiteSpace(challenge));
        Assert.AreEqual("S256", query["code_challenge_method"].ToString());
        Assert.AreEqual("code", query["response_type"].ToString());
        Assert.AreEqual("test-telegram-client", query["client_id"].ToString());
        Assert.AreEqual("openid profile", query["scope"].ToString());

        fixture.Telegram.ExpectedNonce = nonce.ToString();
        var callback = await http.GetAsync("/auth/telegram/callback?code=test-code&state=" +
            Uri.EscapeDataString(state.ToString()));
        Assert.IsTrue(fixture.Telegram.TokenRequestObserved,
            "The callback did not reach the fake upstream token endpoint. Observed endpoints: " + string.Join(", ", fixture.Telegram.Requests));
        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.AreEqual("/clients", callback.Headers.Location?.OriginalString);
        Assert.IsTrue(callback.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.IsTrue(cookies!.Any(x => x.StartsWith("__Host-CommandLineMcp.Session=", StringComparison.Ordinal)));
        Assert.IsTrue(fixture.Telegram.TokenRequestObserved);

        var page = await http.GetAsync("/clients");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.IsTrue((await page.Content.ReadAsStringAsync()).Contains("Клиенты", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoginUsesShortStateAndRestoresLongLocalReturnUrlOnce()
    {
        using var fixture = new AuthHostFixture();
        await fixture.SeedUserAsync(new User { Id = Guid.NewGuid(), TelegramId = 123456789, IsOAuthAllowed = true });
        using var http = fixture.CreateClient();
        var returnUrl = "/connect/authorize?client_id=" + new string('c', 180) +
            "&redirect_uri=https%3A%2F%2Fclient.example%2Fcallback&scope=commands%20offline_access" +
            "&code_challenge=" + new string('x', 64) + "&code_challenge_method=S256&state=" + new string('s', 180);
        var login = await http.GetAsync("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        var state = query["state"].ToString();
        Assert.AreEqual(43, state.Length);
        Assert.IsTrue(query.TryGetValue("nonce", out var nonce) && !string.IsNullOrWhiteSpace(nonce));
        fixture.Telegram.ExpectedNonce = nonce.ToString();

        var callback = await http.GetAsync("/auth/telegram/callback?code=test-code&state=" + Uri.EscapeDataString(state));
        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.AreEqual(returnUrl, callback.Headers.Location?.OriginalString);

        fixture.Telegram.ResetTokenRequestObservation();
        var replay = await http.GetAsync("/auth/telegram/callback?code=test-code&state=" + Uri.EscapeDataString(state));
        Assert.AreEqual("/login?error=telegram", replay.Headers.Location?.OriginalString);
        Assert.IsFalse(fixture.Telegram.TokenRequestObserved);
    }

    [TestMethod]
    public void TelegramStateRejectsTamperedAndExpiredValues()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new TelegramStateStore(clock);
        var format = new TelegramStateDataFormat(store);
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/clients" };
        var state = format.Protect(properties);
        Assert.IsNull(format.Unprotect(state[..^1] + (state[^1] == 'A' ? "B" : "A")));
        Assert.AreEqual("/clients", format.Unprotect(state)?.RedirectUri);
        Assert.IsNull(format.Unprotect(state));

        var expired = format.Protect(new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/clients" });
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsNull(format.Unprotect(expired));
    }

    [TestMethod]
    public async Task LoginDoesNotIssueSessionForTelegramAccountNotAllowedByServer()
    {
        using var fixture = new AuthHostFixture();
        await fixture.SeedUserAsync(new User { Id = Guid.NewGuid(), TelegramId = 123456789, IsOAuthAllowed = false });
        using var http = fixture.CreateClient();
        var login = await http.GetAsync("/login");
        var authorize = login.Headers.Location!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        fixture.Telegram.ExpectedNonce = query["nonce"].ToString();

        var callback = await http.GetAsync("/auth/telegram/callback?code=test-code&state=" +
            Uri.EscapeDataString(query["state"].ToString()));
        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.AreEqual("/login?error=telegram", callback.Headers.Location?.OriginalString);
        Assert.IsFalse(callback.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies!.Any(x => x.StartsWith("__Host-CommandLineMcp.Session=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DynamicRegistrationCreatesConfidentialPkceClientAndRejectsUnsafeMetadata()
    {
        using var fixture = new AuthHostFixture();
        using var http = fixture.CreateClient();
        using var registration = await http.PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { "https://client.example/callback" },
            client_name = "Test MCP client",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "client_secret_post",
            scope = "commands offline_access"
        });
        Assert.AreEqual(HttpStatusCode.Created, registration.StatusCode, await registration.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var clientId = document.RootElement.GetProperty("client_id").GetString();
        var clientSecret = document.RootElement.GetProperty("client_secret").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(clientId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(clientSecret));
        Assert.AreEqual("client_secret_post", document.RootElement.GetProperty("token_endpoint_auth_method").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("grant_types").EnumerateArray()
            .Select(x => x.GetString()).SequenceEqual(new[] { "authorization_code", "refresh_token" }));
        Assert.AreEqual("no-store", registration.Headers.CacheControl?.ToString());

        using var basicRegistration = await http.PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { "https://client.example/basic" },
            token_endpoint_auth_method = "client_secret_basic"
        });
        Assert.AreEqual(HttpStatusCode.Created, basicRegistration.StatusCode,
            await basicRegistration.Content.ReadAsStringAsync());
        using (var basicJson = JsonDocument.Parse(await basicRegistration.Content.ReadAsStringAsync()))
            Assert.AreEqual("client_secret_basic",
                basicJson.RootElement.GetProperty("token_endpoint_auth_method").GetString());

        foreach (var bad in new object[]
        {
            new { redirect_uris = new[] { "https://client.example/*" } },
            new { redirect_uris = new[] { "http://127.0.0.1:4321/callback" } },
            new { redirect_uris = new[] { "http://example.com/callback" } },
            new { redirect_uris = new[] { "javascript:alert(1)" } },
            new { redirect_uris = new[] { "https://client.example/callback#fragment" } },
            new { redirect_uris = new[] { "https://client.example/callback" }, grant_types = new[] { "client_credentials" } },
            new { redirect_uris = new[] { "https://client.example/callback" }, response_types = new[] { "token" } },
            new { redirect_uris = new[] { "https://client.example/callback" }, token_endpoint_auth_method = "none" },
            new { redirect_uris = new[] { "https://client.example/callback" }, scope = "commands admin" }
        })
        {
            using var invalid = await http.PostAsJsonAsync("/register", bad);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode,
                "Unsafe or unsupported dynamic client metadata must be rejected.");
        }

        using var oversized = await http.PostAsync("/register",
            new StringContent("{\"redirect_uris\":[\"https://client.example/callback\"],\"x\":\"" +
                new string('x', 17 * 1024) + "\"}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [TestMethod]
    public async Task DiscoveryAndProtectedResourceMetadataAdvertiseIssuerResourcePkceAndChallengeInvalidBearer()
    {
        using var fixture = new AuthHostFixture();
        using var http = fixture.CreateClient();

        using var discoveryResponse = await http.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);
        using var discovery = JsonDocument.Parse(await discoveryResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("https://localhost/", discovery.RootElement.GetProperty("issuer").GetString());
        Assert.AreEqual("https://localhost/connect/authorize", discovery.RootElement.GetProperty("authorization_endpoint").GetString());
        Assert.AreEqual("https://localhost/connect/token", discovery.RootElement.GetProperty("token_endpoint").GetString());
        CollectionAssert.Contains(discovery.RootElement.GetProperty("code_challenge_methods_supported")
            .EnumerateArray().Select(x => x.GetString()).ToArray(), "S256");

        using var resourceResponse = await http.GetAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.AreEqual(HttpStatusCode.OK, resourceResponse.StatusCode);
        using var resource = JsonDocument.Parse(await resourceResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("https://localhost/mcp", resource.RootElement.GetProperty("resource").GetString());
        CollectionAssert.Contains(resource.RootElement.GetProperty("authorization_servers")
            .EnumerateArray().Select(x => x.GetString()).ToArray(), "https://localhost/");

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-test-token");
        using var protectedResponse = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
        Assert.IsTrue(protectedResponse.Headers.WwwAuthenticate.Any(value =>
            value.Parameter?.Contains("resource_metadata=\"https://localhost/.well-known/oauth-protected-resource/mcp\"", StringComparison.Ordinal) == true),
            "Unauthorized MCP challenges must point to the protected-resource metadata document.");
    }

    [TestMethod]
    public async Task InternalHttpOptInServesDiscoveryAndKeepsPublicOAuthUrisAndMcpProtected()
    {
        using var fixture = new AuthHostFixture(allowHttpTransport: true);
        using var http = fixture.CreateClient("http://localhost");
        using var discoveryResponse = await http.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode, await discoveryResponse.Content.ReadAsStringAsync());
        using var discovery = JsonDocument.Parse(await discoveryResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("https://localhost/", discovery.RootElement.GetProperty("issuer").GetString());
        Assert.AreEqual("https://localhost/connect/authorize", discovery.RootElement.GetProperty("authorization_endpoint").GetString());
        using var protectedResourceResponse = await http.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var protectedResource = JsonDocument.Parse(await protectedResourceResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("https://localhost/mcp", protectedResource.RootElement.GetProperty("resource").GetString());

        var login = await http.GetAsync("/login");
        var authorize = login.Headers.Location!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        Assert.AreEqual("https://localhost/auth/telegram/callback", query["redirect_uri"].ToString());

        using var anonymousMcp = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousMcp.StatusCode);
    }

    [TestMethod]
    public async Task HttpTransportRemainsRejectedByDefault()
    {
        using var fixture = new AuthHostFixture();
        using var http = fixture.CreateClient("http://localhost");
        using var response = await http.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
            "With the opt-in disabled, OpenIddict must reject OAuth metadata requests over HTTP.");
    }

    [TestMethod]
    public async Task PkceAuthorizationRefreshAndRealMcpSdkFlow()
    {
        using var fixture = new AuthHostFixture();
        var userId = Guid.NewGuid();
        const long telegramId = 123456789;
        const string clientBearer = "temporary-test-client-bearer";
        var clientId = Guid.NewGuid();
        await fixture.SeedUserAsync(new User { Id = userId, TelegramId = telegramId, IsOAuthAllowed = true });
        await fixture.SeedClientAsync(new Client
        {
            Id = clientId,
            UserId = userId,
            Name = "MCP integration client",
            BearerKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientBearer))),
            DeletedAt = null
        });
        using (var validationScope = fixture.CreateScope())
        {
            var preflightAccess = await validationScope.ServiceProvider.GetRequiredService<IUserAccessValidator>()
                .IsOAuthAllowedAsync(userId);
            Assert.IsTrue(preflightAccess, "The enabled test user was not found by the server access validator.");
        }

        using (var scope = fixture.CreateScope())
        {
            var scopes = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictScopeManager>();
            var commands = await scopes.FindByNameAsync("commands");
            Assert.IsNotNull(commands, "Commands scope was not provisioned.");
            var resources = await scopes.GetResourcesAsync(commands);
            CollectionAssert.Contains(resources.ToArray(), "https://localhost/mcp");
        }

        using var http = fixture.CreateClient();
        await CompleteTelegramLoginAsync(http, fixture.Telegram);
        var registered = await RegisterAsync(http);

        const string redirectUri = "https://client.example/callback";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorization = new Dictionary<string, string>
        {
            ["client_id"] = registered.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "commands offline_access",
            ["state"] = "oauth-integration-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = "https://localhost/mcp"
        };
        var plainPkce = new Dictionary<string, string>(authorization) { ["code_challenge_method"] = "plain" };
        using (var rejected = await http.GetAsync(AddQueryString("/connect/authorize", plainPkce)))
            AssertRejectedOAuthRequest(rejected);
        var unknownScope = new Dictionary<string, string>(authorization) { ["scope"] = "commands admin" };
        using (var rejected = await http.GetAsync(AddQueryString("/connect/authorize", unknownScope)))
            AssertRejectedOAuthRequest(rejected);
        var wrongResource = new Dictionary<string, string>(authorization) { ["resource"] = "https://other.example/mcp" };
        using (var rejected = await http.GetAsync(AddQueryString("/connect/authorize", wrongResource)))
            AssertRejectedOAuthRequest(rejected);
        var authorizeUrl = AddQueryString("/connect/authorize", authorization);
        using var consent = await http.GetAsync(authorizeUrl);
        Assert.AreEqual(HttpStatusCode.OK, consent.StatusCode, await consent.Content.ReadAsStringAsync());
        Assert.IsTrue(consent.Headers.TryGetValues("Content-Security-Policy", out var cspValues));
        var csp = cspValues!.Single();
        Assert.IsTrue(csp.Contains("form-action 'self' https://client.example", StringComparison.Ordinal));
        Assert.IsFalse(csp.Contains("https://evil.example", StringComparison.Ordinal));
        Assert.AreEqual("no-store", consent.Headers.CacheControl?.ToString());
        Assert.AreEqual("no-referrer", consent.Headers.GetValues("Referrer-Policy").Single());
        var html = await consent.Content.ReadAsStringAsync();
        var csrf = System.Text.RegularExpressions.Regex.Match(html,
            "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(csrf));
        var consentFields = new Dictionary<string, string>(authorization)
        {
            ["decision"] = "allow",
            ["__RequestVerificationToken"] = csrf
        };
        using var granted = await http.PostAsync("/connect/authorize", new FormUrlEncodedContent(consentFields));
        Assert.AreEqual(HttpStatusCode.Redirect, granted.StatusCode, await granted.Content.ReadAsStringAsync());
        var redirectParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(granted.Headers.Location!.Query);
        Assert.AreEqual("oauth-integration-state", redirectParams["state"].ToString());
        var code = redirectParams["code"].ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(code));

        using var tokenResponse = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = registered.ClientId,
            ["client_secret"] = registered.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["resource"] = "https://localhost/mcp"
        }));
        Assert.AreEqual(HttpStatusCode.OK, tokenResponse.StatusCode, await tokenResponse.Content.ReadAsStringAsync());
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;
        var firstRefresh = tokenJson.RootElement.GetProperty("refresh_token").GetString()!;
        Assert.AreEqual("Bearer", tokenJson.RootElement.GetProperty("token_type").GetString());

        using var refreshResponse = await http.PostAsync("/connect/token", RefreshForm(registered, firstRefresh));
        Assert.AreEqual(HttpStatusCode.OK, refreshResponse.StatusCode, await refreshResponse.Content.ReadAsStringAsync());
        using var refreshJson = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
        var rotatedRefresh = refreshJson.RootElement.GetProperty("refresh_token").GetString()!;
        var rotatedAccessToken = refreshJson.RootElement.GetProperty("access_token").GetString()!;
        Assert.AreNotEqual(firstRefresh, rotatedRefresh);

        using var mcpHttp = new HttpClient(fixture.Server.CreateHandler()) { BaseAddress = new Uri("https://localhost") };
        mcpHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotatedAccessToken);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("https://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, mcpHttp, NullLoggerFactory.Instance, ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, new McpClientOptions(), NullLoggerFactory.Instance);
        var tools = await mcp.ListToolsAsync();
        Assert.AreEqual(userId, fixture.AccessObservation.LastUserId);
        Assert.IsTrue(fixture.AccessObservation.LastAllowed);
        CollectionAssert.AreEquivalent(new[] { "list_clients", "submit_job", "get_job", "wait_job", "restart_client" },
            tools.Select(tool => tool.Name).ToArray());
        var listed = await mcp.CallToolAsync("list_clients", new Dictionary<string, object?>());
        Assert.IsTrue(listed.IsError != true);
        Assert.IsTrue(JsonSerializer.Serialize(listed.StructuredContent).Contains(clientId.ToString(), StringComparison.Ordinal));
        var submitted = await mcp.CallToolAsync("submit_job", new Dictionary<string, object?>
        {
            ["clientId"] = clientId.ToString(),
            ["command"] = "Write-Output 'Python PowerShell integration'; exit 0",
            ["workingDirectory"] = null,
            ["timeoutSeconds"] = 30
        });
        Assert.IsTrue(submitted.IsError != true);
        using var submission = JsonDocument.Parse(JsonSerializer.Serialize(submitted.StructuredContent));
        var jobId = submission.RootElement.GetProperty("jobId").GetString()!;

        using (var pythonApi = new HttpClient(fixture.Server.CreateHandler()) { BaseAddress = new Uri("https://localhost") })
        using (var proxy = new TestServerTcpProxy(pythonApi))
        {
            var clientOutput = await RunPythonClientOnceAsync(proxy.BaseAddress, clientBearer, fixture.RootPath, jobId);
            using var clientStatus = JsonDocument.Parse(clientOutput);
            Assert.AreEqual("acknowledged", clientStatus.RootElement.GetProperty("journalState").GetString());
        }

        var getJob = await mcp.CallToolAsync("get_job", new Dictionary<string, object?> { ["jobId"] = jobId });
        Assert.IsTrue(getJob.IsError != true);
        using var getResult = JsonDocument.Parse(JsonSerializer.Serialize(getJob.StructuredContent));
        Assert.AreEqual("Succeeded", getResult.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("Python PowerShell integration", getResult.RootElement.GetProperty("stdout").GetString()?.Trim());
        Assert.AreEqual(0, getResult.RootElement.GetProperty("exitCode").GetInt32());
        var waitJob = await mcp.CallToolAsync("wait_job", new Dictionary<string, object?>
        {
            ["jobId"] = jobId,
            ["waitSeconds"] = 0
        });
        Assert.IsTrue(waitJob.IsError != true);
        using var waitResult = JsonDocument.Parse(JsonSerializer.Serialize(waitJob.StructuredContent));
        Assert.AreEqual("Succeeded", waitResult.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("Python PowerShell integration", waitResult.RootElement.GetProperty("stdout").GetString()?.Trim());

        await fixture.SetUserAllowedAsync(userId, false);
        using var deniedMcp = await mcpHttp.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedMcp.StatusCode);
        using var disabledUserRefresh = await http.PostAsync("/connect/token", RefreshForm(registered, rotatedRefresh));
        Assert.AreEqual(HttpStatusCode.BadRequest, disabledUserRefresh.StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientBearer);
        using var pollBeforeRevoke = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.NoContent, pollBeforeRevoke.StatusCode);
        await fixture.DisableClientAsync(userId, clientId);
        using var pollAfterRevoke = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.Unauthorized, pollAfterRevoke.StatusCode);
    }

    [TestMethod]
    public async Task ReusingRefreshTokenRevokesItsAuthorization()
    {
        using var fixture = new AuthHostFixture();
        await fixture.SeedUserAsync(new User { Id = Guid.NewGuid(), TelegramId = 123456789, IsOAuthAllowed = true });
        using var http = fixture.CreateClient();
        await CompleteTelegramLoginAsync(http, fixture.Telegram);
        var client = await RegisterAsync(http);
        var issued = await AuthorizeAndExchangeAsync(http, client);
        using var refreshed = await http.PostAsync("/connect/token", RefreshForm(client, issued.RefreshToken));
        Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());

        using var replay = await http.PostAsync("/connect/token", RefreshForm(client, issued.RefreshToken));
        Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();
        using var scope = fixture.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>();
        var oldToken = await tokens.FindByReferenceIdAsync(issued.RefreshToken);
        Assert.IsNotNull(oldToken);
        Assert.AreEqual(OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked,
            await tokens.GetStatusAsync(oldToken));
        var authorizationId = await tokens.GetAuthorizationIdAsync(oldToken);
        var authorizations = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.FindByIdAsync(authorizationId!);
        Assert.IsNotNull(authorization);
        Assert.AreEqual(OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked,
            await authorizations.GetStatusAsync(authorization),
            "Refresh replay must revoke its authorization. Token endpoint response: " + replayBody);
    }

    [TestMethod]
    public async Task RevokingBearerClientPreventsFurtherLongPolls()
    {
        using var fixture = new AuthHostFixture();
        var userId = Guid.NewGuid();
        const string key = "temporary-test-client-bearer";
        var clientId = Guid.NewGuid();
        await fixture.SeedUserAsync(new User { Id = userId, TelegramId = 123456789, IsOAuthAllowed = true });
        await fixture.SeedClientAsync(new Client
        {
            Id = clientId,
            UserId = userId,
            Name = "bearer-revocation-client",
            BearerKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
            DeletedAt = null
        });

        using var http = fixture.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var before = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.NoContent, before.StatusCode);
        await fixture.DisableClientAsync(userId, clientId);
        using var after = await http.PostAsJsonAsync("/api/client/jobs/poll", new { waitSeconds = 0 });
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [TestMethod]
    public async Task DisablingUserRevokesTheirAuthorizationAndBlocksRefresh()
    {
        using var fixture = new AuthHostFixture();
        var userId = Guid.NewGuid();
        await fixture.SeedUserAsync(new User { Id = userId, TelegramId = 123456789, IsOAuthAllowed = true });
        using var http = fixture.CreateClient();
        await CompleteTelegramLoginAsync(http, fixture.Telegram);
        var client = await RegisterAsync(http);
        var issued = await AuthorizeAndExchangeAsync(http, client);

        await fixture.SetUserAllowedAsync(userId, false);
        using var refresh = await http.PostAsync("/connect/token", RefreshForm(client, issued.RefreshToken));
        Assert.AreEqual(HttpStatusCode.BadRequest, refresh.StatusCode, await refresh.Content.ReadAsStringAsync());

        using var scope = fixture.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>();
        var refreshToken = await tokens.FindByReferenceIdAsync(issued.RefreshToken);
        Assert.IsNotNull(refreshToken);
        var authorizationId = await tokens.GetAuthorizationIdAsync(refreshToken);
        var authorizations = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.FindByIdAsync(authorizationId!);
        Assert.IsNotNull(authorization);
        Assert.AreEqual(OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked,
            await authorizations.GetStatusAsync(authorization));
    }

    private static async Task CompleteTelegramLoginAsync(HttpClient http, FakeTelegramOidcHandler telegram)
    {
        var login = await http.GetAsync("/login");
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode, await login.Content.ReadAsStringAsync());
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        telegram.ExpectedNonce = query["nonce"].ToString();
        var callback = await http.GetAsync("/auth/telegram/callback?code=test-code&state=" + Uri.EscapeDataString(query["state"].ToString()));
        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode, await callback.Content.ReadAsStringAsync());
        Assert.AreEqual("/clients", callback.Headers.Location?.OriginalString);
    }

    private static async Task<RegisteredClient> RegisterAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { "https://client.example/callback" },
            client_name = "OAuth integration client",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "client_secret_post",
            scope = "commands offline_access"
        });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new RegisteredClient(json.RootElement.GetProperty("client_id").GetString()!,
            json.RootElement.GetProperty("client_secret").GetString()!);
    }

    private static async Task<IssuedTokens> AuthorizeAndExchangeAsync(HttpClient http, RegisteredClient client)
    {
        const string redirectUri = "https://client.example/callback";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "commands offline_access",
            ["state"] = "disabled-user-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = "https://localhost/mcp"
        };
        using var consent = await http.GetAsync(AddQueryString("/connect/authorize", query));
        Assert.AreEqual(HttpStatusCode.OK, consent.StatusCode, await consent.Content.ReadAsStringAsync());
        var html = await consent.Content.ReadAsStringAsync();
        var csrf = System.Text.RegularExpressions.Regex.Match(html,
            "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value;
        var fields = new Dictionary<string, string>(query)
        {
            ["decision"] = "allow",
            ["__RequestVerificationToken"] = csrf
        };
        using var granted = await http.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields));
        Assert.AreEqual(HttpStatusCode.Redirect, granted.StatusCode, await granted.Content.ReadAsStringAsync());
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(granted.Headers.Location!.Query)["code"].ToString();
        using var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["resource"] = "https://localhost/mcp"
        }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new IssuedTokens(json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.GetProperty("refresh_token").GetString()!);
    }

    private static FormUrlEncodedContent RefreshForm(RegisteredClient client, string refreshToken) =>
        new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["resource"] = "https://localhost/mcp"
        });

    private static string AddQueryString(string path, IReadOnlyDictionary<string, string> values) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(path,
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value));

    private static void AssertRejectedOAuthRequest(HttpResponseMessage response)
    {
        Assert.IsTrue(response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is { } location &&
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query).ContainsKey("error"),
            "The server must reject an unsupported scope, resource, or PKCE method.");
    }

    private static async Task<string> RunPythonClientOnceAsync(Uri serverUri, string bearerKey,
        string temporaryDirectory, string jobId)
    {
        var workspace = FindWorkspaceRoot();
        var clientDirectory = Path.Combine(workspace, "mcp-client");
        var statePath = Path.Combine(temporaryDirectory, "python-client-state.sqlite3");
        const string script = """
            import json, os
            from pathlib import Path
            from commandline_mcp_client.client import HttpApi, Journal, Runner
            from commandline_mcp_client.shells import PowerShellShell
            journal = Journal(Path(os.environ['TEST_STATE_PATH']))
            try:
                Runner(HttpApi(os.environ['TEST_SERVER_URL'], os.environ['TEST_BEARER_KEY']), journal,
                       PowerShellShell(), wait_seconds=1).run_once()
                print(json.dumps({'journalState': journal.state(os.environ['TEST_JOB_ID'])}))
            finally:
                journal.close()
            """;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("python")
            {
                WorkingDirectory = clientDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.Environment["TEST_STATE_PATH"] = statePath;
        process.StartInfo.Environment["TEST_SERVER_URL"] = serverUri.ToString().TrimEnd('/');
        process.StartInfo.Environment["TEST_BEARER_KEY"] = bearerKey;
        process.StartInfo.Environment["TEST_JOB_ID"] = jobId;
        Assert.IsTrue(process.Start(), "Python client process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exit = process.WaitForExitAsync();
        if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(60))) != exit)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Python client did not finish its single polling cycle within 60 seconds.");
        }
        await exit;
        Assert.AreEqual(0, process.ExitCode, "Python client failed: " + await stderr);
        return await stdout;
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "mcp-client", "pyproject.toml"))) return directory.FullName;
        Assert.Fail("Could not locate mcp-client from the test output directory.");
        return string.Empty;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record RegisteredClient(string ClientId, string ClientSecret);
    private sealed record IssuedTokens(string AccessToken, string RefreshToken);

    private sealed class AuthHostFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CommandLineMcp.Tests", Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;

        public FakeTelegramOidcHandler Telegram { get; } = new();
        public AccessObservation AccessObservation { get; } = new();
        public TestServer Server => _factory.Server;
        public string RootPath => _root;

        public AuthHostFixture(bool allowHttpTransport = false)
        {
            Directory.CreateDirectory(_root);
            using var certificateKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", certificateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            const string password = "temporary-test-only-password";
            Assert.IsTrue(certificate.HasPrivateKey);
            var pfx = certificate.Export(X509ContentType.Pfx, password);
            using (var imported = X509CertificateLoader.LoadPkcs12(pfx, password,
                       X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable))
            {
                Assert.IsTrue(imported.HasPrivateKey);
                Assert.IsTrue(imported.NotAfter.ToUniversalTime() > DateTime.UtcNow);
                Assert.IsTrue(imported.NotBefore.ToUniversalTime() <= DateTime.UtcNow);
            }
            var secretsFile = Path.Combine(_root, "server.secrets.json");
            File.WriteAllText(secretsFile, JsonSerializer.Serialize(new
            {
                OAuth = new
                {
                    Issuer = "https://localhost/",
                    Resource = "https://localhost/mcp",
                    SecretsFile = secretsFile,
                    DataProtectionKeysPath = Path.Combine(_root, "keys"),
                    SigningCertificate = new { PfxBase64 = Convert.ToBase64String(pfx), Password = password }
                },
                Telegram = new { ClientId = "test-telegram-client", ClientSecret = "test-telegram-client-secret" }
            }));
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = $"Data Source={Path.Combine(_root, "integration.db")}",
                ["OAuth:SecretsFile"] = secretsFile,
                ["OAuth:Issuer"] = "https://localhost/",
                ["OAuth:Resource"] = "https://localhost/mcp",
                ["OAuth:AllowHttpTransport"] = allowHttpTransport.ToString(),
                ["OAuth:DataProtectionKeysPath"] = Path.Combine(_root, "keys"),
                ["OAuth:SigningCertificate:PfxBase64"] = Convert.ToBase64String(pfx),
                ["OAuth:SigningCertificate:Password"] = password,
                ["Telegram:ClientId"] = "test-telegram-client",
                ["Telegram:ClientSecret"] = "test-telegram-client-secret"
            };
            CryptographicOperations.ZeroMemory(pfx);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(config));
                foreach (var (key, value) in config)
                    if (value is not null) builder.UseSetting(key, value);
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureTestServices(services => services.PostConfigure<OpenIdConnectOptions>(
                    TelegramAuthentication.TelegramScheme, options =>
                    {
                        options.Backchannel = new HttpClient(Telegram, disposeHandler: false);
                        options.BackchannelHttpHandler = Telegram;
                        var metadata = "https://oauth.telegram.org/.well-known/openid-configuration";
                        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(metadata,
                            new OpenIdConnectConfigurationRetriever(),
                            new HttpDocumentRetriever(new HttpClient(Telegram, disposeHandler: false)) { RequireHttps = true });
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IUserAccessValidator>();
                    services.AddSingleton(AccessObservation);
                    services.AddScoped<IUserAccessValidator, ObservedUserAccessValidator>();
                });
            });
        }

        public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        public HttpClient CreateClient(string baseAddress) => _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        public async Task SeedUserAsync(User user)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CommandLineMcpDbContext>();
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        public async Task SeedClientAsync(Client client)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CommandLineMcpDbContext>();
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        public async Task SetUserAllowedAsync(Guid userId, bool allowed)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CommandLineMcpDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.IsOAuthAllowed = allowed;
            await db.SaveChangesAsync();
        }

        public async Task DisableClientAsync(Guid userId, Guid clientId)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CommandLineMcpDbContext>();
            var client = await db.Clients.SingleAsync(x => x.Id == clientId && x.UserId == userId);
            client.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        public AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();


        public void Dispose()
        {
            _factory.Dispose();
            Telegram.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class AccessObservation
    {
        public Guid? LastUserId { get; set; }
        public bool LastAllowed { get; set; }
    }

    private sealed class ObservedUserAccessValidator(IUserRepository users, AccessObservation observation) : IUserAccessValidator
    {
        public async Task<bool> IsOAuthAllowedAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var allowed = (await users.FindByIdAsync(userId, cancellationToken))?.IsOAuthAllowed == true;
            observation.LastUserId = userId;
            observation.LastAllowed = allowed;
            return allowed;
        }
    }

    private sealed class TestServerTcpProxy : IDisposable
    {
        private readonly TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly HttpClient _backend;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;

        public Uri BaseAddress { get; }

        public TestServerTcpProxy(HttpClient backend)
        {
            _backend = backend;
            _listener.Start();
            var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            _acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    await HandleRequestAsync(connection, _shutdown.Token);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (SocketException) when (_shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        }

        private async Task HandleRequestAsync(TcpClient connection, CancellationToken cancellationToken)
        {
            var stream = connection.GetStream();
            var headerBytes = new List<byte>();
            var singleByte = new byte[1];
            while (headerBytes.Count < 32 * 1024)
            {
                var count = await stream.ReadAsync(singleByte.AsMemory(), cancellationToken);
                if (count == 0) return;
                headerBytes.Add(singleByte[0]);
                var length = headerBytes.Count;
                if (length >= 4 && headerBytes[length - 4] == 13 && headerBytes[length - 3] == 10 &&
                    headerBytes[length - 2] == 13 && headerBytes[length - 1] == 10) break;
            }
            if (headerBytes.Count >= 32 * 1024) throw new InvalidDataException("HTTP request headers exceeded the test proxy limit.");

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = lines[0].Split(' ', 3);
            if (requestLine.Length < 2) throw new InvalidDataException("Malformed HTTP request line.");
            var contentLength = 0;
            string? contentType = null;
            var request = new HttpRequestMessage(new HttpMethod(requestLine[0]), new Uri(_backend.BaseAddress!, requestLine[1]));
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var name = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
                else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                else if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                         !name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(name, value);
            }
            if (contentLength > 0)
            {
                var body = new byte[contentLength];
                var offset = 0;
                while (offset < body.Length)
                {
                    var count = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                    if (count == 0) throw new EndOfStreamException("HTTP request ended before its content-length.");
                    offset += count;
                }
                request.Content = new ByteArrayContent(body);
                if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            using (request)
            using (var response = await _backend.SendAsync(request, cancellationToken))
            {
                var responseBody = response.Content is null ? [] : await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentTypeValue = response.Content?.Headers.ContentType?.ToString();
                var reason = response.ReasonPhrase ?? "Response";
                var responseHeaders = new StringBuilder()
                    .Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ').Append(reason).Append("\r\n")
                    .Append("Content-Length: ").Append(responseBody.Length).Append("\r\n")
                    .Append("Connection: close\r\n");
                if (!string.IsNullOrEmpty(contentTypeValue)) responseHeaders.Append("Content-Type: ").Append(contentTypeValue).Append("\r\n");
                responseHeaders.Append("\r\n");
                var bytes = Encoding.ASCII.GetBytes(responseHeaders.ToString());
                await stream.WriteAsync(bytes, cancellationToken);
                if (responseBody.Length > 0) await stream.WriteAsync(responseBody, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try { _acceptLoop.GetAwaiter().GetResult(); } catch (SocketException) { }
            _shutdown.Dispose();
            _backend.Dispose();
        }
    }

    private sealed class FakeTelegramOidcHandler : HttpMessageHandler
    {
        private readonly RSA _key = RSA.Create(2048);
        private const string Kid = "commandline-test-telegram-key";
        public string? ExpectedNonce { get; set; }
        public bool TokenRequestObserved { get; private set; }
        public List<string> Requests { get; } = [];

        public void ResetTokenRequestObservation() => TokenRequestObserved = false;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.AbsolutePath);
            if (uri.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return Json(new
                {
                    issuer = "https://oauth.telegram.org",
                    authorization_endpoint = "https://oauth.telegram.org/auth",
                    token_endpoint = "https://oauth.telegram.org/token",
                    jwks_uri = "https://oauth.telegram.org/jwks",
                    response_types_supported = new[] { "code" },
                    subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "RS256" },
                    token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" }
                });
            if (uri.AbsolutePath == "/jwks")
            {
                var parameters = _key.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = Kid, alg = "RS256", n = B64Url(parameters.Modulus!), e = B64Url(parameters.Exponent!) } } });
            }
            if (uri.AbsolutePath == "/token")
            {
                TokenRequestObserved = true;
                var token = CreateIdToken(ExpectedNonce ?? string.Empty);
                return Json(new { access_token = "temporary-test-access-token", token_type = "Bearer", expires_in = 300, id_token = token });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string CreateIdToken(string nonce)
        {
            var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = Kid }));
            var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = "https://oauth.telegram.org",
                aud = "test-telegram-client",
                sub = "123456789",
                id = "123456789",
                name = "Test Telegram User",
                nonce,
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
            }));
            var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
            var signature = _key.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return header + "." + payload + "." + B64Url(signature);
        }

        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        });

        private static string B64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        protected override void Dispose(bool disposing)
        {
            if (disposing) _key.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialTime;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
