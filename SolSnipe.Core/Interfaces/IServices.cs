using SolSnipe.Core.Models;

namespace SolSnipe.Core.Interfaces;

public interface IWalletMonitor
{
    Task StartAsync(IEnumerable<string> walletAddresses, CancellationToken ct);
    Task StopAsync();
    Task AddWalletAsync(string address);
    Task RemoveWalletAsync(string address);
    event Func<string, string, string, Task>? OnWalletBuy;

    public interface ITradeExecutor
    {
        Task<TradeResult> BuyTokenAsync(string tokenMint, double amountSol, int slippageBps);
        Task<TradeResult> SellTokenAsync(string tokenMint, double tokenAmount, int slippageBps);
        Task<double?> GetQuotedOutputAsync(string inputMint, string outputMint, double inputAmount);
    }

    public interface IPositionManager
    {
        Task OpenPositionAsync(TokenSignal signal, TradeResult buyResult, double amountSol);
        Task CheckAndExitPositionsAsync(CancellationToken ct);
        Task<List<Position>> GetOpenPositionsAsync();
        Task<List<Position>> GetClosedPositionsAsync(int limit = 50);
    }

    public interface IPriceService
    {
        Task<double?> GetTokenPriceUsdAsync(string tokenMint);
        Task<double?> GetMarketCapUsdAsync(string tokenMint);
        Task<double?> GetLiquidityUsdAsync(string tokenMint);
        Task<DateTime?> GetTokenCreatedAtAsync(string tokenMint);
    }

    public interface IWalletScorer
    {
        Task<WalletScore> ScoreWalletAsync(string address);
        Task<List<WalletScore>> ScoreAllCandidatesAsync(List<CandidateWallet> candidates, CancellationToken ct);
    }

    public interface IWalletDiscovery
    {
        Task<List<CandidateWallet>> DiscoverCandidatesAsync(CancellationToken ct);
    }

    public interface ISignalAggregator
    {
        void RecordBuy(string walletAddress, string tokenMint);
        TokenSignal? GetSignal(string tokenMint);
        List<TokenSignal> GetAllSignals();
        void PruneOldSignals(TimeSpan maxAge);
        event Func<TokenSignal, Task>? OnThresholdReached;
    }
}