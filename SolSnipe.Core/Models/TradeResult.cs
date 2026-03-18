namespace SolSnipe.Core.Models;

public class TradeResult
{
    public bool Success { get; set; }
    public string? TxSignature { get; set; }
    public string? ErrorMessage { get; set; }
    public double TokenAmount { get; set; }     
    public double ExecutedPriceUsd { get; set; }
    public double SlippagePct { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}