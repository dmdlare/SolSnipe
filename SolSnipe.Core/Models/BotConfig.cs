namespace SolSnipe.Core.Models;

public class BotConfig
{
    public SolanaConfig Solana { get; set; } = new();
    public HeliusConfig Helius { get; set; } = new();
    public TradingConfig Bot { get; set; } = new();
    public WalletScorerConfig WalletScorer { get; set; } = new();
}

public class SolanaConfig
{
    public string RpcUrl { get; set; } = string.Empty;
    public string WsUrl { get; set; } = string.Empty;
    public string WalletPrivateKey { get; set; } = string.Empty;  // base58
}

public class HeliusConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string WebhookCallbackUrl { get; set; } = string.Empty; // your public URL
}

public class TradingConfig
{
  
    public bool PaperTrading { get; set; } = true;

    public double PaperTradingStartingBalanceSol { get; set; } = 10.0;

    public double TriggerThresholdPct { get; set; } = 60;
    public double BuyAmountSol { get; set; } = 0.5;
    public double TakeProfitPct { get; set; } = 50;
    public double StopLossPct { get; set; } = 20;
    public int MaxOpenPositions { get; set; } = 5;
    public int SlippageBps { get; set; } = 100;           
    public double MinMarketCapUsd { get; set; } = 100_000;
    public double MaxMarketCapUsd { get; set; } = 50_000_000;
    public double MinLiquidityUsd { get; set; } = 50_000;
    public int MinTokenAgeMinutes { get; set; } = 60;       
    public int PositionCheckIntervalSeconds { get; set; } = 30;
    public int MaxHoldTimeHours { get; set; } = 24;
    public int SignalWindowMinutes { get; set; } = 10;    
}

public class WalletScorerConfig
{
    public int MaxTrackedWallets { get; set; } = 15;
    public string RescoringCronSchedule { get; set; } = "0 0 0 ? * SUN";
    public int MinTradesForScoring { get; set; } = 20;
    public int LookbackDays { get; set; } = 90;
    public int CandidatesPerTrendingToken { get; set; } = 20;
    public int TrendingTokensToScan { get; set; } = 10;
}