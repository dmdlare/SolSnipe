using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;

public class PositionManagerService : IPositionManager
{
    private readonly ITradeExecutor _executor;
    private readonly IPriceService _prices;
    private readonly IPositionRepository _posRepo;
    private readonly ITradeHistoryRepository _historyRepo;
    private readonly TradingConfig _config;
    private readonly ILogger<PositionManagerService> _logger;

    public PositionManagerService(
        ITradeExecutor executor,
        IPriceService prices,
        IPositionRepository posRepo,
        ITradeHistoryRepository historyRepo,
        IOptions<TradingConfig> config,
        ILogger<PositionManagerService> logger)
    {
        _executor = executor;
        _prices = prices;
        _posRepo = posRepo;
        _historyRepo = historyRepo;
        _config = config.Value;
        _logger = logger;
    }

    public async Task OpenPositionAsync(TokenSignal signal, TradeResult buyResult, double amountSol)
    {
        var position = new Position
        {
            TokenMint = signal.TokenMint,
            TokenSymbol = signal.TokenSymbol,
            EntryPriceUsd = buyResult.ExecutedPriceUsd,
            TokenAmount = buyResult.TokenAmount,
            AmountSolSpent = amountSol,
            BuyTxSignature = buyResult.TxSignature!,
            OpenedAt = DateTime.UtcNow,
            Status = PositionStatus.Open,
            SignalBuyerCount = signal.BuyerWallets.Count,
            SignalTotalWallets = signal.TotalTrackedWallets,
            SignalStrengthPct = signal.SignalStrengthPct,
            IsPaperTrade = buyResult.TxSignature?.StartsWith("PAPER_") == true,
        };

        _posRepo.Upsert(position);

        _logger.LogInformation(
            "📂 Position opened: {Symbol} | Entry: ${Price:F8} | Amount: {Amount} tokens | Signal: {Pct:F0}%",
            position.TokenSymbol, position.EntryPriceUsd,
            position.TokenAmount, position.SignalStrengthPct);
    }

    public async Task CheckAndExitPositionsAsync(CancellationToken ct)
    {
        var openPositions = _posRepo.GetOpen();
        if (openPositions.Count == 0) return;

        _logger.LogDebug("Checking {Count} open positions", openPositions.Count);

        foreach (var position in openPositions)
        {
            if (ct.IsCancellationRequested) break;
            try { await CheckPositionAsync(position); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking position {Mint}", position.TokenMint[..8] + "...");
            }
        }
    }

    private async Task CheckPositionAsync(Position position)
    {
        var currentPrice = await _prices.GetTokenPriceUsdAsync(position.TokenMint);
        if (currentPrice is null) return;

        var pnlPct = (currentPrice.Value - position.EntryPriceUsd) / position.EntryPriceUsd * 100;
        var holdHours = (DateTime.UtcNow - position.OpenedAt).TotalHours;

        _logger.LogInformation(
            "📊 {Symbol} | Entry: ${Entry:F8} | Now: ${Now:F8} | PnL: {Pnl:+0.0;-0.0}% | Hold: {Hold:F1}h",
            position.TokenSymbol, position.EntryPriceUsd, currentPrice.Value, pnlPct, holdHours);

        ExitReason? exitReason = null;

        if (pnlPct >= _config.TakeProfitPct)
        {
            _logger.LogInformation("🎯 Take profit hit: {Pnl:+0.0}%", pnlPct);
            exitReason = Models.ExitReason.TakeProfit;
        }
        else if (pnlPct <= -_config.StopLossPct)
        {
            _logger.LogInformation("🛑 Stop loss hit: {Pnl:0.0}%", pnlPct);
            exitReason = Models.ExitReason.StopLoss;
        }
        else if (holdHours >= _config.MaxHoldTimeHours)
        {
            _logger.LogInformation("⏱️ Time expiry hit after {Hold:F1}h", holdHours);
            exitReason = Models.ExitReason.TimeExpiry;
        }

        if (exitReason.HasValue)
            await ExitPositionAsync(position, currentPrice.Value, pnlPct, exitReason.Value);
    }

    private async Task ExitPositionAsync(
        Position position, double exitPrice, double pnlPct, ExitReason reason)
    {
        _logger.LogInformation("Exiting position {Symbol} — Reason: {Reason}", position.TokenSymbol, reason);

        var sellResult = await _executor.SellTokenAsync(
            position.TokenMint,
            position.TokenAmount,
            _config.SlippageBps);

        if (!sellResult.Success)
        {
            _logger.LogError("Sell failed for {Symbol}: {Err}", position.TokenSymbol, sellResult.ErrorMessage);
            return;
        }

        var pnlUsd = (exitPrice - position.EntryPriceUsd) * position.TokenAmount;

        position.Status = PositionStatus.Closed;
        position.ExitPriceUsd = exitPrice;
        position.SellTxSignature = sellResult.TxSignature;
        position.ClosedAt = DateTime.UtcNow;
        position.ExitReason = reason;
        position.PnlUsd = pnlUsd;
        position.PnlPct = pnlPct;

        _posRepo.Upsert(position);
        _historyRepo.Insert(position);

        var emoji = pnlUsd >= 0 ? "💰" : "💸";
        _logger.LogInformation(
            "{Emoji} CLOSED {Symbol} | PnL: {Pnl:+0.00;-0.00}% / ${PnlUsd:+0.00;-0.00} | Reason: {Reason} | TX: {Sig}",
            emoji, position.TokenSymbol, pnlPct, pnlUsd, reason,
            sellResult.TxSignature?[..16] + "...");
    }

    public Task<List<Position>> GetOpenPositionsAsync() =>
        Task.FromResult(_posRepo.GetOpen());

    public Task<List<Position>> GetClosedPositionsAsync(int limit = 50) =>
        Task.FromResult(_posRepo.GetClosed(limit));
}