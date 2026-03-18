using SolSnipe.Core.Models;

namespace SolSnipe.Core.Interfaces;



public interface IPositionRepository
{
    void Upsert(Position position);
    List<Position> GetOpen();
    List<Position> GetClosed(int limit = 50);
    Position? GetByMint(string tokenMint);
    bool HasOpenPosition(string tokenMint);
}


public interface IWalletRepository
{
    void Upsert(TrackedWallet wallet);
    void UpsertMany(IEnumerable<TrackedWallet> wallets);
    List<TrackedWallet> GetActive();
    List<TrackedWallet> GetAll();
    TrackedWallet? GetByAddress(string address);
    void Deactivate(string address);
    int Count();
}



public interface ITradeHistoryRepository
{
    void Insert(Position closedPosition);
    List<Position> GetRecent(int limit = 50);
    double GetTotalPnlUsd();
    double GetWinRate();
    TradeSummary GetSummary();
}



public class TradeSummary
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public double WinRatePct { get; set; }
    public double TotalPnlUsd { get; set; }
    public double AvgWinPct { get; set; }
    public double AvgLossPct { get; set; }
    public double BestTradePnlUsd { get; set; }
    public double WorstTradePnlUsd { get; set; }
}