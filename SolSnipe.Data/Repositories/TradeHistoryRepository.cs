using LiteDB;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;

namespace SolSnipe.Data.Repositories;

public class TradeHistoryRepository : ITradeHistoryRepository
{
    private readonly ILiteCollection<Position> _col;

    public TradeHistoryRepository(SolSnipeDb db)
    {
        _col = db.GetCollection<Position>("trade_history");
        _col.EnsureIndex(x => x.ClosedAt);
        _col.EnsureIndex(x => x.TokenMint);
    }

    public void Insert(Position closedPosition) => _col.Insert(closedPosition);

    public List<Position> GetRecent(int limit = 50) =>
        _col.FindAll()
            .OrderByDescending(x => x.ClosedAt)
            .Take(limit)
            .ToList();

    public double GetTotalPnlUsd() =>
        _col.FindAll().Sum(x => x.PnlUsd ?? 0);

    public double GetWinRate()
    {
        var all = _col.FindAll().ToList();
        if (all.Count == 0) return 0;
        return (double)all.Count(x => x.PnlPct > 0) / all.Count * 100;
    }

    public TradeSummary GetSummary()
    {
        var all = _col.FindAll().ToList();
        var wins = all.Where(x => x.PnlPct > 0).ToList();
        var losses = all.Where(x => x.PnlPct <= 0).ToList();

        return new TradeSummary
        {
            TotalTrades = all.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,
            WinRatePct = all.Count == 0 ? 0 : (double)wins.Count / all.Count * 100,
            TotalPnlUsd = all.Sum(x => x.PnlUsd ?? 0),
            AvgWinPct = wins.Count == 0 ? 0 : wins.Average(x => x.PnlPct ?? 0),
            AvgLossPct = losses.Count == 0 ? 0 : losses.Average(x => x.PnlPct ?? 0),
            BestTradePnlUsd = all.Count == 0 ? 0 : all.Max(x => x.PnlUsd ?? 0),
            WorstTradePnlUsd = all.Count == 0 ? 0 : all.Min(x => x.PnlUsd ?? 0),
        };
    }
}