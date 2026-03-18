namespace SolSnipe.Core.Models;

public class WalletScore
{
    public string Address { get; set; } = string.Empty;
    public double TotalScore { get; set; }          
    public double WinRateScore { get; set; }
    public double AvgProfitScore { get; set; }
    public double LossControlScore { get; set; }
    public double HoldTimeScore { get; set; }
    public double EntrySpeedScore { get; set; }
    public double RugAvoidanceScore { get; set; }

   
    public double WinRatePct { get; set; }
    public double AvgProfitOnWinPct { get; set; }
    public double AvgLossOnLossPct { get; set; }
    public double AvgHoldTimeMinutes { get; set; }
    public double AvgEntryMinutesAfterLaunch { get; set; }
    public double RugAvoidancePct { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public DateTime ScoredAt { get; set; } = DateTime.UtcNow;
}

public class CandidateWallet
{
    public string Address { get; set; } = string.Empty;
    public string DiscoveredFrom { get; set; } = string.Empty; 
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}

public class WalletSwapRecord
{
    public string TokenMint { get; set; } = string.Empty;
    public string TokenSymbol { get; set; } = string.Empty;
    public double EntryPriceUsd { get; set; }
    public double ExitPriceUsd { get; set; }
    public double PnlPct { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public double HoldTimeMinutes => (ExitTime - EntryTime).TotalMinutes;
    public bool IsWin => PnlPct > 0;
    public bool WasRug { get; set; }    
    public bool ExitedBeforeRug { get; set; }
    public double MinutesAfterLaunch { get; set; }
}