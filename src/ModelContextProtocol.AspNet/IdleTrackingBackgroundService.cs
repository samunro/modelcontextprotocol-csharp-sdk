using Microsoft.Extensions.Hosting;

namespace ModelContextProtocol.AspNet;

internal sealed class IdleTrackingBackgroundService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
