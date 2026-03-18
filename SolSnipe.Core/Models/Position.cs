namespace SolSnipe.Core.Models;

public enum PositionStatus { Open, Closed }
public enum ExitReason { TakeProfit, StopLoss, TimeExpiry, Manual }

public class Position
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TokenMint { get; set; } = string.Empty;
    public string TokenSymbol { get; set; } = string.Empty;
    public double EntryPriceUsd { get; set; }
    public double TokenAmount { get; set; }
    public double AmountSolSpent { get; set; }
    public string BuyTxSignature { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    
    public bool IsPaperTrade { get; set; } = false;

    public int SignalBuyerCount { get; set; }
    public int SignalTotalWallets { get; set; }
    public double SignalStrengthPct { get; set; }

    
    public PositionStatus Status { get; set; } = PositionStatus.Open;
    public double? ExitPriceUsd { get; set; }
    public string? SellTxSignature { get; set; }
    public DateTime? ClosedAt { get; set; }
    public ExitReason? ExitReason { get; set; }
    public double? PnlUsd { get; set; }
    public double? PnlPct { get; set; }

    public TimeSpan HoldTime => (ClosedAt ?? DateTime.UtcNow) - OpenedAt;
}