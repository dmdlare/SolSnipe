using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Core.Services;
using SolSnipe.Dashboard.Models;

namespace SolSnipe.Dashboard.Controllers;

public class DashboardController : Controller
{
    private readonly IPositionManager _positions;
    private readonly ISignalAggregator _signals;
    private readonly IWalletRepository _wallets;
    private readonly ITradeHistoryRepository _history;
    private readonly IPriceService _prices;
    private readonly TradingConfig _config;
    private readonly BotStateService _botState;

    public DashboardController(
        IPositionManager positions,
        ISignalAggregator signals,
        IWalletRepository wallets,
        ITradeHistoryRepository history,
        IPriceService prices,
        IOptions<TradingConfig> config,
        BotStateService botState)
    {
        _positions = positions;
        _signals = signals;
        _wallets = wallets;
        _history = history;
        _prices = prices;
        _config = config.Value;
        _botState = botState;
    }

    public async Task<IActionResult> Index()
    {
        var openPositions = await _positions.GetOpenPositionsAsync();
        var closedTrades = await _positions.GetClosedPositionsAsync(10);
        var allSignals = _signals.GetAllSignals();
        var summary = _history.GetSummary();

        // Enrich open positions with live prices
        var openVms = new List<PositionViewModel>();
        foreach (var pos in openPositions)
        {
            var price = await _prices.GetTokenPriceUsdAsync(pos.TokenMint);
            openVms.Add(PositionViewModel.FromPosition(pos, price));
        }

        var vm = new DashboardViewModel
        {
            BotRunning = _botState.IsRunning,
            PaperTrading = _config.PaperTrading,
            VirtualBalance = _botState.VirtualBalance,
            OpenPositionCount = openPositions.Count,
            TotalTradesCount = summary.TotalTrades,
            TotalPnlUsd = summary.TotalPnlUsd,
            WinRatePct = summary.WinRatePct,
            ProfitFactor = CalculateProfitFactor(summary),
            TrackedWalletCount = _wallets.GetActive().Count,
            ActiveSignalCount = allSignals.Count,
            OpenPositions = openVms,
            RecentTrades = closedTrades.Select(p => PositionViewModel.FromPosition(p)).ToList(),
            LiveSignals = allSignals.Select(SignalViewModel.FromSignal).ToList(),
        };

        return View(vm);
    }

    [HttpPost]
    public IActionResult ToggleBot()
    {
        _botState.Toggle();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult ToggleMode()
    {
        _botState.TogglePaperMode();
        TempData["Message"] = _botState.PaperTrading
            ? "Switched to PAPER trading mode"
            : "Switched to LIVE trading mode";
        return RedirectToAction(nameof(Index));
    }

    // JSON endpoint polled by JS every 15s for live P&L
    [HttpGet]
    public async Task<IActionResult> LivePnl()
    {
        var openPositions = await _positions.GetOpenPositionsAsync();
        var result = new List<object>();

        foreach (var pos in openPositions)
        {
            var price = await _prices.GetTokenPriceUsdAsync(pos.TokenMint);
            if (price is null) continue;

            var pnlPct = (price.Value - pos.EntryPriceUsd) / pos.EntryPriceUsd * 100;
            var pnlUsd = (price.Value - pos.EntryPriceUsd) * pos.TokenAmount;

            result.Add(new { id = pos.Id, tokenMint = pos.TokenMint, currentPrice = price.Value, pnlPct, pnlUsd });
        }

        return Json(result);
    }

    private static double CalculateProfitFactor(TradeSummary s)
    {
        if (s.TotalTrades == 0) return 0;
        var grossWins = s.WinningTrades * Math.Abs(s.AvgWinPct);
        var grossLosses = s.LosingTrades * Math.Abs(s.AvgLossPct);
        return grossLosses == 0 ? 999 : grossWins / grossLosses;
    }
}