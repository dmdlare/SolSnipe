using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;


namespace SolSnipe.Core.Services;


public class PaperTradingReportService
{
    private readonly ITradeHistoryRepository _history;
    private readonly IPositionRepository _positions;
    private readonly TradingConfig _config;
    private readonly ILogger<PaperTradingReportService> _logger;

    public PaperTradingReportService(
        ITradeHistoryRepository history,
        IPositionRepository positions,
        IOptions<TradingConfig> config,
        ILogger<PaperTradingReportService> logger)
    {
        _history = history;
        _positions = positions;
        _config = config.Value;
        _logger = logger;
    }

    public void PrintReport()
    {
        var closed = _history.GetRecent(1000)
                             .Where(p => p.IsPaperTrade)
                             .ToList();

        var open = _positions.GetOpen()
                             .Where(p => p.IsPaperTrade)
                             .ToList();

        var startingBalance = _config.PaperTradingStartingBalanceSol;
        var totalTrades = closed.Count;
        var wins = closed.Where(p => p.PnlPct > 0).ToList();
        var losses = closed.Where(p => p.PnlPct <= 0).ToList();
        var winRate = totalTrades == 0 ? 0 : (double)wins.Count / totalTrades * 100;
        var totalPnlUsd = closed.Sum(p => p.PnlUsd ?? 0);
        var avgWinPct = wins.Count == 0 ? 0 : wins.Average(p => p.PnlPct ?? 0);
        var avgLossPct = losses.Count == 0 ? 0 : losses.Average(p => p.PnlPct ?? 0);
        var bestTrade = closed.Count == 0 ? null : closed.MaxBy(p => p.PnlUsd ?? 0);
        var worstTrade = closed.Count == 0 ? null : closed.MinBy(p => p.PnlUsd ?? 0);
        var avgHoldMinutes = closed.Count == 0 ? 0 : closed.Average(p => p.HoldTime.TotalMinutes);

        // Exit reason breakdown
        var tpCount = closed.Count(p => p.ExitReason == ExitReason.TakeProfit);
        var slCount = closed.Count(p => p.ExitReason == ExitReason.StopLoss);
        var expCount = closed.Count(p => p.ExitReason == ExitReason.TimeExpiry);

        // Profit factor: gross wins / gross losses
        var grossWins = wins.Sum(p => Math.Abs(p.PnlUsd ?? 0));
        var grossLosses = losses.Sum(p => Math.Abs(p.PnlUsd ?? 0));
        var profitFactor = grossLosses == 0 ? double.PositiveInfinity : grossWins / grossLosses;

        // Consecutive stats
        var (maxConsecWins, maxConsecLosses) = GetConsecutiveStats(closed);

        _logger.LogInformation("");
        _logger.LogInformation("╔══════════════════════════════════════════════════════╗");
        _logger.LogInformation("║           PAPER TRADING PERFORMANCE REPORT           ║");
        _logger.LogInformation("╠══════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  Period:   {Start} → {End}",
            closed.Count > 0 ? closed.Min(p => p.OpenedAt).ToString("yyyy-MM-dd") : "N/A",
            closed.Count > 0 ? closed.Max(p => p.ClosedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd") : "N/A");
        _logger.LogInformation("║  Starting virtual balance: {Bal:F2} SOL", startingBalance);
        _logger.LogInformation("╠══════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  TRADE SUMMARY");
        _logger.LogInformation("║  Total trades:     {Total}", totalTrades);
        _logger.LogInformation("║  Winning trades:   {Wins}  ({WinPct:F1}%)", wins.Count, winRate);
        _logger.LogInformation("║  Losing trades:    {Losses}  ({LossPct:F1}%)", losses.Count, 100 - winRate);
        _logger.LogInformation("║  Open positions:   {Open}", open.Count);
        _logger.LogInformation("╠══════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  PROFIT & LOSS");
        _logger.LogInformation("║  Total PnL:        {Sign}{Abs:F2} USD",
            totalPnlUsd >= 0 ? "+" : "", totalPnlUsd);
        _logger.LogInformation("║  Avg win:          +{AvgWin:F1}%", avgWinPct);
        _logger.LogInformation("║  Avg loss:         -{AvgLoss:F1}%", Math.Abs(avgLossPct));
        _logger.LogInformation("║  Profit factor:    {PF:F2}  (>1.5 is good, >2.0 is great)",
            profitFactor == double.PositiveInfinity ? 999 : profitFactor);
        _logger.LogInformation("╠══════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  EXIT REASONS");
        _logger.LogInformation("║  Take profit:      {TP} trades", tpCount);
        _logger.LogInformation("║  Stop loss:        {SL} trades", slCount);
        _logger.LogInformation("║  Time expiry:      {Exp} trades", expCount);
        _logger.LogInformation("╠══════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  RISK METRICS");
        _logger.LogInformation("║  Avg hold time:    {Hold:F0} minutes", avgHoldMinutes);
        _logger.LogInformation("║  Max consec wins:  {W}", maxConsecWins);
        _logger.LogInformation("║  Max consec losses:{L}", maxConsecLosses);

        if (bestTrade is not null)
            _logger.LogInformation("║  Best trade:       +${Pnl:F2} ({Token}) +{Pct:F1}%",
                bestTrade.PnlUsd ?? 0, bestTrade.TokenSymbol, bestTrade.PnlPct ?? 0);

        if (worstTrade is not null)
            _logger.LogInformation("║  Worst trade:      -${Pnl:F2} ({Token}) {Pct:F1}%",
                Math.Abs(worstTrade.PnlUsd ?? 0), worstTrade.TokenSymbol, worstTrade.PnlPct ?? 0);

        _logger.LogInformation("╠══════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  VERDICT");

        var verdict = GetVerdict(winRate, profitFactor, totalTrades);
        _logger.LogInformation("║  {Verdict}", verdict.Text);
        _logger.LogInformation("║  {Rec}", verdict.Recommendation);
        _logger.LogInformation("╚══════════════════════════════════════════════════════╝");
        _logger.LogInformation("");

        // Print individual trades table
        if (closed.Count > 0)
        {
            _logger.LogInformation("CLOSED TRADES (most recent 20):");
            _logger.LogInformation("{Header}",
                $"{"#",-4} {"Token",-12} {"Entry $",-14} {"Exit $",-14} {"PnL%",-10} {"PnL USD",-12} {"Hold",-12} {"Exit"}");
            _logger.LogInformation(new string('─', 88));

            foreach (var (trade, i) in closed.TakeLast(20).Select((t, i) => (t, i + 1)))
            {
                var pnlSign = (trade.PnlPct ?? 0) >= 0 ? "+" : "";
                _logger.LogInformation(
                    "{Num,-4} {Token,-12} ${Entry,-13:F8} ${Exit,-13:F8} {Sign}{Pct,-9:F1}% {PnlSign}{PnlUsd,-11:F2} {Hold,-12} {Reason}",
                    i,
                    (trade.TokenSymbol.Length > 10 ? trade.TokenSymbol[..10] : trade.TokenSymbol).PadRight(10),
                    trade.EntryPriceUsd,
                    trade.ExitPriceUsd ?? 0,
                    pnlSign,
                    trade.PnlPct ?? 0,
                    (trade.PnlUsd ?? 0) >= 0 ? "+" : "",
                    trade.PnlUsd ?? 0,
                    FormatHoldTime(trade.HoldTime),
                    trade.ExitReason?.ToString() ?? "?");
            }

            _logger.LogInformation("");
        }

        SaveReportToFile(closed, open, winRate, totalPnlUsd, profitFactor, verdict);
    }

   
    // Private helpers
    
    private static (int MaxWins, int MaxLosses) GetConsecutiveStats(List<Position> trades)
    {
        int maxW = 0, maxL = 0, curW = 0, curL = 0;
        foreach (var t in trades.OrderBy(t => t.ClosedAt))
        {
            if ((t.PnlPct ?? 0) > 0) { curW++; curL = 0; maxW = Math.Max(maxW, curW); }
            else { curL++; curW = 0; maxL = Math.Max(maxL, curL); }
        }
        return (maxW, maxL);
    }

    private static string FormatHoldTime(TimeSpan ts)
    {
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:F0}m";
        if (ts.TotalHours < 24) return $"{ts.TotalHours:F1}h";
        return $"{ts.TotalDays:F1}d";
    }

    private record Verdict(string Text, string Recommendation);

    private static Verdict GetVerdict(double winRate, double profitFactor, int totalTrades)
    {
        if (totalTrades < 10)
            return new("⏳ Not enough trades yet to draw conclusions.",
                       "Run for at least 2 weeks before going live.");

        if (profitFactor >= 2.0 && winRate >= 55)
            return new("✅ STRONG — System is performing well.",
                       "Consider going live with a small amount (0.1 SOL/trade).");

        if (profitFactor >= 1.5 && winRate >= 45)
            return new("✅ DECENT — System is profitable.",
                       "You can go live but start small and monitor closely.");

        if (profitFactor >= 1.0)
            return new("⚠️  MARGINAL — System is barely breaking even.",
                       "Tune trigger threshold or wallet list before going live.");

        return new("❌ UNPROFITABLE — System is losing money in simulation.",
                   "Do NOT go live. Review wallet quality and filters.");
    }

    private void SaveReportToFile(
        List<Position> closed, List<Position> open,
        double winRate, double totalPnlUsd, double profitFactor,
        Verdict verdict)
    {
        try
        {
            Directory.CreateDirectory("reports");
            var filename = $"reports/paper_trading_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";

            var lines = new List<string>
            {
                $"SOLSNIPE PAPER TRADING REPORT — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC",
                new string('=', 60),
                $"Total trades:   {closed.Count}",
                $"Win rate:       {winRate:F1}%",
                $"Total PnL:      ${totalPnlUsd:+0.00;-0.00}",
                $"Profit factor:  {(profitFactor == double.PositiveInfinity ? "∞" : profitFactor.ToString("F2"))}",
                $"Verdict:        {verdict.Text}",
                $"Recommendation: {verdict.Recommendation}",
                "",
                "TRADE LOG:",
                $"{"#",-4} {"Token",-12} {"Entry",-16} {"Exit",-16} {"PnL%",-10} {"PnL $",-12} {"Hold",-10} {"Reason"}",
                new string('-', 90),
            };

            lines.AddRange(closed.Select((t, i) =>
                $"{i + 1,-4} {t.TokenSymbol,-12} ${t.EntryPriceUsd,-15:F8} ${t.ExitPriceUsd ?? 0,-15:F8} " +
                $"{(t.PnlPct ?? 0):+0.0;-0.0}%{"",4} ${t.PnlUsd ?? 0:+0.00;-0.00}{"",6} " +
                $"{FormatHoldTime(t.HoldTime),-10} {t.ExitReason}"
            ));

            File.WriteAllLines(filename, lines);
            _logger.LogInformation("Report saved to {File}", filename);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not save report file: {Err}", ex.Message);
        }
    }
}