using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ut2004Stats.Core.Services;

public class StatsOptions
{
    public const string SectionName = "Stats";

    /// <summary>Directory the game server writes its stat logs to.</summary>
    public string LogDirectory { get; set; } = "/data/logs";

    /// <summary>How often to sweep the directory for newly finished matches.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Import everything already present when the app starts.</summary>
    public bool ScanOnStartup { get; set; } = true;
}

/// <summary>
/// Watches the stats directory and imports matches as they finish, so the site
/// stays current without anyone having to trigger a parse by hand.
/// </summary>
public class LogWatcherService(
    IServiceScopeFactory scopeFactory,
    IOptions<StatsOptions> options,
    ILogger<LogWatcherService> logger) : BackgroundService
{
    private readonly StatsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Watching {Directory} for new matches every {Interval}",
            _options.LogDirectory, _options.ScanInterval);

        if (!_options.ScanOnStartup)
            await Task.Delay(_options.ScanInterval, stoppingToken);

        using var timer = new PeriodicTimer(_options.ScanInterval);

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var importer = scope.ServiceProvider.GetRequiredService<MatchImporter>();
                await importer.ImportDirectoryAsync(_options.LogDirectory, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad sweep kill the watcher — try again next tick.
                logger.LogError(ex, "Scan of {Directory} failed", _options.LogDirectory);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
