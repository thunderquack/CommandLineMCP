using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CommandLineMcp.Server;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class ForwardedHeadersConfigurationTests
{
    [TestMethod]
    public void TrustAllProxies_EnablesForwardedHeadersWithoutKnownProxyRestrictions()
    {
        var options = Configure(("ForwardedHeaders:TrustAllProxies", "true"),
            ("ForwardedHeaders:KnownProxies:0", "192.0.2.10"));

        Assert.AreEqual(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                        ForwardedHeaders.XForwardedHost, options.ForwardedHeaders);
        Assert.AreEqual(1, options.ForwardLimit);
        Assert.AreEqual(0, options.KnownProxies.Count);
        Assert.AreEqual(0, options.KnownIPNetworks.Count);
    }

    [TestMethod]
    public void TrustAllProxiesFalse_WithKnownProxy_UsesOnlyConfiguredProxy()
    {
        var options = Configure(("ForwardedHeaders:TrustAllProxies", "false"),
            ("ForwardedHeaders:KnownProxies:0", "192.0.2.10"));

        Assert.AreEqual(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                        ForwardedHeaders.XForwardedHost, options.ForwardedHeaders);
        Assert.AreEqual(1, options.ForwardLimit);
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("192.0.2.10") }, options.KnownProxies.ToArray());
        Assert.AreEqual(0, options.KnownIPNetworks.Count);
    }

    [TestMethod]
    public void TrustAllProxiesFalse_WithoutKnownProxies_DisablesForwardedHeaders()
    {
        var options = Configure(("ForwardedHeaders:TrustAllProxies", "false"));

        Assert.AreEqual(ForwardedHeaders.None, options.ForwardedHeaders);
        Assert.AreEqual(1, options.ForwardLimit);
        Assert.AreEqual(0, options.KnownProxies.Count);
        Assert.AreEqual(0, options.KnownIPNetworks.Count);
    }

    private static ForwardedHeadersOptions Configure(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value))
            .Build();
        var services = new ServiceCollection();
        services.AddTrustedForwardedHeaders(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }
}
