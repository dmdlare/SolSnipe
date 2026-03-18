namespace SolSnipe.Core.Models;

public class TokenSignal
{
    public string TokenMint { get; set; } = string.Empty;
    public string TokenSymbol { get; set; } = string.Empty;
    public List<string> BuyerWallets { get; set; } = new();
    public int TotalTrackedWallets { get; set; }
    public double SignalStrengthPct => TotalTrackedWallets == 0 ? 0
        : (double)BuyerWallets.Count / TotalTrackedWallets * 100;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public double? PriceAtFirstSignal { get; set; }
    public double? MarketCapUsd { get; set; }
    public double? LiquidityUsd { get; set; }
    public bool TriggeredBuy { get; set; } = false;
}