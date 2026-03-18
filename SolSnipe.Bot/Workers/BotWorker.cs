using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Helpers;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Core.Services;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Bot.Workers;


public class BotWorker : BackgroundService
{
    private readonly IWalletMonitor _monitor;
    private readonly ISignalAggregator _signals;
    private readonly ITradeExecutor _executor;
    private readonly IPositionManager _positions;
    private readonly IPriceService _prices;
    private readonly FilterHelper _filter;
    private readonly IWalletRepository _wallets;
    private readonly IPositionRepository _posRepo;
    private readonly PaperTradingReportService _report;
    private readonly WalletScorerWorker _scorer;
    private readonly TradingConfig _config;
    private readonly ILogger<BotWorker> _logger;
    private readonly IDashboardNotifier? _notifier;

    public BotWorker(
        IWalletMonitor monitor,
        ISignalAggregator signals,
        ITradeExecutor executor,
        IPositionManager positions,
        IPriceService prices,
        FilterHelper filter,
        IWalletRepository wallets,
        IPositionRepository posRepo,
        PaperTradingReportService report,
        WalletScorerWorker scorer,
        IOptions<TradingConfig> config,
        ILogger<BotWorker> logger,
        IDashboardNotifier? notifier = null)
    {
        _monitor = monitor;
        _signals = signals;
        _executor = executor;
        _positions = positions;
        _prices = prices;
        _filter = filter;
        _wallets = wallets;
        _posRepo = posRepo;
        _report = report;
        _scorer = scorer;
        _config = config.Value;
        _logger = logger;
        _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
      
        _logger.LogInformation("SOLSNIPE BOT STARTING");
        

        if (_config.PaperTrading)
        {
           
            _logger.LogInformation("PAPER TRADING MODE");
           
            _logger.LogInformation("Virtual balance: {Bal,6:F2} SOL", _config.PaperTradingStartingBalanceSol);
           
        }
        else
        {
            _logger.LogWarning("LIVE MODE");
        }

        // Auto discover wallets on first launch
        var trackedWallets = _wallets.GetActive();

        if (trackedWallets.Count == 0)
        {
           
            _logger.LogInformation("|  Running automatic wallet discovery & scoring...  |");
           

            await _scorer.RunAsync(ct);
            trackedWallets = _wallets.GetActive();

            if (trackedWallets.Count == 0)
            {
                _logger.LogError("Wallet discovery returned no results.");
                _logger.LogError("Possible causes:");
                _logger.LogError("  - DexScreener is temporarily unavailable");
                _logger.LogError("  - Helius API key is missing or invalid");
                _logger.LogError("  - No trending Solana tokens found");
                _logger.LogError("Bot will retry discovery in 10 minutes...");

                // Retry once after 10 minutes rather than crashing
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                await _scorer.RunAsync(ct);
                trackedWallets = _wallets.GetActive();

                if (trackedWallets.Count == 0)
                {
                    _logger.LogError("Discovery failed again. Check your Helius API key and try restarting.");
                    return;
                }
            }
        }

        //Show wallet list 
        _logger.LogInformation("Tracking {Count} wallets:", trackedWallets.Count);
        foreach (var w in trackedWallets)
            _logger.LogInformation("  {Label} | Score: {Score:F0} | Swaps: {Trades} | Tokens: {WR:F0}",
                w.Label,
                w.Address[..8],
                w.Score,
                w.WinRate);

        if (trackedWallets.All(w => w.Score <= 35 && w.WinRate == 0))
        {
            _logger.LogWarning("");
            _logger.LogWarning("*** All wallets have score 35 and 0% win rate ***");
            _logger.LogWarning("This means the Helius API key is not configured.");
            _logger.LogWarning("Wallets are being monitored but are NOT ranked by");
            _logger.LogWarning("profitability yet. Set Helius:ApiKey in appsettings.json");
            _logger.LogWarning("and restart to get proper scoring.");
            _logger.LogWarning("");
        }

        // Recover any open positions from a previous run 
        var openPositions = await _positions.GetOpenPositionsAsync();
        if (openPositions.Count > 0)
            _logger.LogInformation("Recovered {Count} open position(s) from previous session", openPositions.Count);

        //Wire signals > trades 
        _signals.OnThresholdReached += OnSignalTriggeredAsync;

        // Start wallet monitor 
        await _monitor.StartAsync(trackedWallets.Select(w => w.Address), ct);

        //Background signal pruning 
        _ = Task.Run(() => PruneSignalsLoopAsync(ct), ct);

        if (_notifier != null)
            await _notifier.SendBotStatusChange(true, _config.PaperTrading);

       
        _logger.LogInformation("Bot is live");
        _logger.LogInformation("  Trigger:   {Pct}% of wallets must buy", _config.TriggerThresholdPct);
        _logger.LogInformation("  Buy size:  {Sol} SOL per trade", _config.BuyAmountSol);
        _logger.LogInformation("  Take profit: +{TP}%", _config.TakeProfitPct);
        _logger.LogInformation("  Stop loss:   -{SL}%", _config.StopLossPct);
       

        await Task.Delay(Timeout.Infinite, ct);
    }

    // Signal > Buy
    private async Task OnSignalTriggeredAsync(TokenSignal signal)
    {
        _logger.LogInformation(
            "[SIGNAL] {Mint}... | {Count}/{Total} wallets ({Pct:F0}%) | threshold {Threshold}%",
            signal.TokenMint[..8],
            signal.BuyerWallets.Count,
            signal.TotalTrackedWallets,
            signal.SignalStrengthPct,
            _config.TriggerThresholdPct);

        // Already holding?
        if (_posRepo.HasOpenPosition(signal.TokenMint))
        {
            _logger.LogInformation("Already holding {Mint}... - skipping", signal.TokenMint[..8]);
            return;
        }

        // Max positions reached?
        if (_posRepo.GetOpen().Count >= _config.MaxOpenPositions)
        {
            _logger.LogWarning("Max open positions ({Max}) reached - skipping", _config.MaxOpenPositions);
            return;
        }

        // Token passes all safety filters?
        var (isValid, reason) = await _filter.IsValidTokenAsync(signal.TokenMint);
        if (!isValid)
        {
            _logger.LogInformation("Token {Mint}... rejected: {Reason}", signal.TokenMint[..8], reason);
            return;
        }

        // Fetch price for position record
        var price = await _prices.GetTokenPriceUsdAsync(signal.TokenMint);

        _logger.LogInformation("Executing buy: {Sol} SOL > {Mint}...",
            _config.BuyAmountSol, signal.TokenMint[..8]);

        if (_notifier != null)
            await _notifier.SendLogMessage("TRADE",
                $"BUY signal: {signal.TokenMint[..8]}... {signal.BuyerWallets.Count}/{signal.TotalTrackedWallets} wallets");

        var result = await _executor.BuyTokenAsync(
            signal.TokenMint,
            _config.BuyAmountSol,
            _config.SlippageBps);

        if (!result.Success)
        {
            _logger.LogError("Buy failed: {Err}", result.ErrorMessage);
            return;
        }

        result.ExecutedPriceUsd = price ?? 0;
        await _positions.OpenPositionAsync(signal, result, _config.BuyAmountSol);
    }

    //Prune old signals every 5 minutes 
    private async Task PruneSignalsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            _signals.PruneOldSignals(TimeSpan.FromMinutes(_config.SignalWindowMinutes * 3));
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Bot stopping...");

        if (_notifier != null)
            await _notifier.SendBotStatusChange(false, _config.PaperTrading);

        if (_config.PaperTrading)
        {
            _logger.LogInformation("Generating paper trading report...");
            _report.PrintReport();
        }

        await _monitor.StopAsync();
        await base.StopAsync(ct);
    }
}