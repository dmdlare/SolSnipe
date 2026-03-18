using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;

namespace SolSnipe.Bot.Workers;
public class PositionMonitorWorker : BackgroundService
{
    private readonly IPositionManager _positions;
    private readonly TradingConfig _config;
    private readonly ILogger<PositionMonitorWorker> _logger;

    public PositionMonitorWorker(
        IPositionManager positions,
        IOptions<TradingConfig> config,
        ILogger<PositionMonitorWorker> logger)
    {
        _positions = positions;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Position monitor started (checking every {Secs}s)",
            _config.PositionCheckIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _positions.CheckAndExitPositionsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Position monitor error");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_config.PositionCheckIntervalSeconds), ct);
        }
    }
}