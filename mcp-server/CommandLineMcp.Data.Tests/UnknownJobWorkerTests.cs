using CommandLineMcp.Data;
using CommandLineMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CommandLineMcp.Data.Tests;

[TestClass]
public sealed class UnknownJobWorkerTests
{
    [TestMethod]
    public async Task WorkerExpiresAllStaleJobsThroughRepositoryAndStopsOnCancellation()
    {
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        var invoked = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Setup(x => x.ExpireStaleRunningAsync(null, It.IsAny<CancellationToken>()))
            .Returns<Guid?, CancellationToken>((clientId, _) =>
            {
                invoked.TrySetResult(clientId);
                return Task.FromResult(2);
            });
        var services = new ServiceCollection();
        services.AddSingleton(jobs.Object);
        await using var provider = services.BuildServiceProvider();
        using var worker = new UnknownJobWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UnknownJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        Assert.IsNull(await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(stop.Token);

        jobs.Verify(x => x.ExpireStaleRunningAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        jobs.VerifyNoOtherCalls();
    }
}
