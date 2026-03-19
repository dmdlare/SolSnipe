using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Dashboard.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Dashboard.Controllers;

public class PositionsController : Controller
{
    private readonly IPositionManager _positions;
    private readonly ITradeExecutor _executor;
    private readonly IPriceService _prices;
    private readonly IPositionRepository _posRepo;
    private readonly ITradeHistoryRepository _history;
    private readonly TradingConfig _config;

    public PositionsController(
        IPositionManager positions,
        ITradeExecutor executor,
        IPriceService prices,
        IPositionRepository posRepo,
        ITradeHistoryRepository history,
        IOptions<TradingConfig> config)
    {
        _positions = positions;
        _executor = executor;
        _prices = prices;
        _posRepo = posRepo;
        _history = history;
        _config = config.Value;
    }

    // Open positions page
    public async Task<IActionResult> Index()
    {
        var open = await _positions.GetOpenPositionsAsync();
        var vms = new List<PositionViewModel>();

        foreach (var pos in open)
        {
            var price = await _prices.GetTokenPriceUsdAsync(pos.TokenMint);
            vms.Add(PositionViewModel.FromPosition(pos, price));
        }

        return View(vms);
    }

    // Trade history page
    public async Task<IActionResult> History(int page = 1)
    {
        const int pageSize = 25;
        var all = await _positions.GetClosedPositionsAsync(500);
        var paged = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var summary = _history.GetSummary();

        var grossWins = summary.WinningTrades * Math.Abs(summary.AvgWinPct);
        var grossLosses = summary.LosingTrades * Math.Abs(summary.AvgLossPct);
        var pf = grossLosses == 0 ? 999.0 : grossWins / grossLosses;

        var vm = new StatsViewModel
        {
            TotalTrades = summary.TotalTrades,
            WinningTrades = summary.WinningTrades,
            LosingTrades = summary.LosingTrades,
            WinRatePct = summary.WinRatePct,
            TotalPnlUsd = summary.TotalPnlUsd,
            AvgWinPct = summary.AvgWinPct,
            AvgLossPct = summary.AvgLossPct,
            BestTradePnlUsd = summary.BestTradePnlUsd,
            WorstTradePnlUsd = summary.WorstTradePnlUsd,
            ProfitFactor = pf,
            RecentTrades = paged.Select(p => PositionViewModel.FromPosition(p)).ToList(),
        };

        ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(all.Count / (double)pageSize);

        return View(vm);
    }

    // Manually close an open position
    [HttpPost]
    public async Task<IActionResult> Close(string id)
    {
        var pos = _posRepo.GetOpen().FirstOrDefault(p => p.Id == id);
        if (pos is null)
        {
            TempData["Error"] = "Position not found or already closed.";
            return RedirectToAction(nameof(Index));
        }

        var result = await _executor.SellTokenAsync(pos.TokenMint, pos.TokenAmount, _config.SlippageBps);
        if (!result.Success)
        {
            TempData["Error"] = $"Close failed: {result.ErrorMessage}";
            return RedirectToAction(nameof(Index));
        }

        var currentPrice = await _prices.GetTokenPriceUsdAsync(pos.TokenMint);
        var exitPrice = currentPrice ?? pos.EntryPriceUsd;
        var pnlPct = (exitPrice - pos.EntryPriceUsd) / pos.EntryPriceUsd * 100;
        var pnlUsd = (exitPrice - pos.EntryPriceUsd) * pos.TokenAmount;

        pos.Status = PositionStatus.Closed;
        pos.ExitPriceUsd = exitPrice;
        pos.SellTxSignature = result.TxSignature;
        pos.ClosedAt = DateTime.UtcNow;
        pos.ExitReason = ExitReason.Manual;
        pos.PnlPct = pnlPct;
        pos.PnlUsd = pnlUsd;

        _posRepo.Upsert(pos);
        _history.Insert(pos);

        TempData["Message"] = $"Position closed. PnL: {(pnlPct >= 0 ? "+" : "")}{pnlPct:F1}% / ${pnlUsd:+0.00;-0.00}";
        return RedirectToAction(nameof(Index));
    }
}