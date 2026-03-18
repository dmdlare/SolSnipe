namespace SolSnipe.Core.Models;

public class TrackedWallet
{
    public string Address { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;       
    public double Score { get; set; }                        
    public double WinRate { get; set; }
    public double AvgProfitPct { get; set; }
    public double AvgLossPct { get; set; }
    public int TotalTrades { get; set; }
    public DateTime LastScored { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}