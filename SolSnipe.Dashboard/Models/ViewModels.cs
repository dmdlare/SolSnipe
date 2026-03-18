using SolSnipe.Core.Models;

namespace SolSnipe.Dashboard.Models;

// ─────────────────────────────────────────────────────────────
// Dashboard overview page model
// Used by: Views/Dashboard/Index.cshtml
// ─────────────────────────────────────────────────────────────
public class DashboardViewModel
{
    public bool BotRunning { get; set; }
    public bool PaperTrading { get; set; }
    public double VirtualBalance { get; set; }
    public int OpenPositionCount { get; set; }
    public int TotalTradesCount { get; set; }
    public double TotalPnlUsd { get; set; }
    public double WinRatePct { get; set; }
    public double ProfitFactor { get; set; }
    public int TrackedWalletCount { get; set; }
    public int ActiveSignalCount { get; set; }
    public List<PositionViewModel> OpenPositions { get; set; } = new();
    public List<PositionViewModel> RecentTrades { get; set; } = new();
    public List<SignalViewModel> LiveSignals { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────
// Single position (open or closed)
// Used by: Views/Dashboard/Index.cshtml
//          Views/Positions/Index.cshtml
//          Views/Positions/History.cshtml
// ─────────────────────────────────────────────────────────────
public class PositionViewModel
{
    public string Id { get; set; } = string.Empty;
    public string TokenMint { get; set; } = string.Empty;
    public string TokenSymbol { get; set; } = string.Empty;

    // Prices
    public double EntryPriceUsd { get; set; }
    public double? ExitPriceUsd { get; set; }       // null while position is open
    public double? CurrentPriceUsd { get; set; }    // live price for open positions

    // Size
    public double TokenAmount { get; set; }
    public double AmountSolSpent { get; set; }

    // P&L
    public double? PnlUsd { get; set; }
    public double? PnlPct { get; set; }

    // Timing
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string HoldTime { get; set; } = string.Empty;

    // Metadata
    public bool IsPaperTrade { get; set; }
    public string? ExitReason { get; set; }
    public string Status { get; set; } = string.Empty;
    public double SignalStrengthPct { get; set; }

    // Computed helpers for Razor views
    public string PnlClass => (PnlPct ?? 0) >= 0 ? "positive" : "negative";
    public string PnlSign => (PnlPct ?? 0) >= 0 ? "+" : "";

    // ── Factory ───────────────────────────────────────────────
    public static PositionViewModel FromPosition(Position p, double? currentPrice = null)
    {
        // For open positions use live price for P&L; for closed use recorded exit price
        var pnlPct = p.Status == PositionStatus.Open && currentPrice.HasValue
            ? (currentPrice.Value - p.EntryPriceUsd) / p.EntryPriceUsd * 100
            : p.PnlPct;

        var pnlUsd = p.Status == PositionStatus.Open && currentPrice.HasValue
            ? (currentPrice.Value - p.EntryPriceUsd) * p.TokenAmount
            : p.PnlUsd;

        var hold = p.Status == PositionStatus.Open
            ? DateTime.UtcNow - p.OpenedAt
            : (p.ClosedAt ?? DateTime.UtcNow) - p.OpenedAt;

        return new PositionViewModel
        {
            Id = p.Id,
            TokenMint = p.TokenMint,
            TokenSymbol = string.IsNullOrEmpty(p.TokenSymbol)
                                   ? p.TokenMint[..8] + "..." : p.TokenSymbol,
            EntryPriceUsd = p.EntryPriceUsd,
            ExitPriceUsd = p.ExitPriceUsd,          // ← was missing
            CurrentPriceUsd = currentPrice,
            TokenAmount = p.TokenAmount,
            AmountSolSpent = p.AmountSolSpent,
            PnlUsd = pnlUsd,
            PnlPct = pnlPct,
            OpenedAt = p.OpenedAt,
            ClosedAt = p.ClosedAt,
            HoldTime = FormatHold(hold),
            IsPaperTrade = p.IsPaperTrade,
            ExitReason = p.ExitReason?.ToString(),
            Status = p.Status.ToString(),
            SignalStrengthPct = p.SignalStrengthPct,
        };
    }

    private static string FormatHold(TimeSpan ts)
    {
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m";
        if (ts.TotalHours < 24) return $"{ts.TotalHours:F1}h";
        return $"{ts.TotalDays:F1}d";
    }
}

// ─────────────────────────────────────────────────────────────
// Live signal card
// Used by: Views/Dashboard/Index.cshtml
//          Views/Signals/Index.cshtml
// ─────────────────────────────────────────────────────────────
public class SignalViewModel
{
    public string TokenMint { get; set; } = string.Empty;
    public string TokenSymbol { get; set; } = string.Empty;
    public int BuyerCount { get; set; }
    public int TotalWallets { get; set; }
    public double SignalStrengthPct { get; set; }
    public bool TriggeredBuy { get; set; }
    public string FirstSeen { get; set; } = string.Empty;
    public string LastUpdated { get; set; } = string.Empty;
    public double? PriceAtSignal { get; set; }

    // "high" / "medium" / "low" — drives CSS colour classes
    public string StrengthClass => SignalStrengthPct >= 75 ? "high"
        : SignalStrengthPct >= 50 ? "medium" : "low";

    public static SignalViewModel FromSignal(TokenSignal s) => new()
    {
        TokenMint = s.TokenMint,
        TokenSymbol = string.IsNullOrEmpty(s.TokenSymbol)
                                ? s.TokenMint[..8] + "..." : s.TokenSymbol,
        BuyerCount = s.BuyerWallets.Count,
        TotalWallets = s.TotalTrackedWallets,
        SignalStrengthPct = s.SignalStrengthPct,
        TriggeredBuy = s.TriggeredBuy,
        FirstSeen = TimeAgo(s.FirstSeenAt),
        LastUpdated = TimeAgo(s.LastUpdatedAt),
        PriceAtSignal = s.PriceAtFirstSignal,
    };

    private static string TimeAgo(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalSeconds < 60) return $"{(int)diff.TotalSeconds}s ago";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        return $"{(int)diff.TotalHours}h ago";
    }
}

// ─────────────────────────────────────────────────────────────
// Single tracked wallet row
// Used by: Views/Wallets/Index.cshtml
// ─────────────────────────────────────────────────────────────
public class WalletViewModel
{
    public string Address { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Score { get; set; }
    public double WinRate { get; set; }
    public double AvgProfitPct { get; set; }
    public double AvgLossPct { get; set; }
    public int TotalTrades { get; set; }
    public DateTime LastScored { get; set; }
    public bool IsActive { get; set; }

    // CSS colour class: "high" / "medium" / "low"
    public string ScoreClass => Score >= 70 ? "high" : Score >= 50 ? "medium" : "low";

    // Shortened address for display: "ABC123...XY99"
    public string ShortAddress => Address.Length > 12
        ? Address[..6] + "..." + Address[^4..] : Address;
}

// ─────────────────────────────────────────────────────────────
// Form model for adding a wallet manually
// Used by: Views/Wallets/Index.cshtml (POST form)
// ─────────────────────────────────────────────────────────────
public class AddWalletRequest
{
    public string Address { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────
// Trade history page — stats + paginated trade list
// Used by: Views/Positions/History.cshtml
// ─────────────────────────────────────────────────────────────
public class StatsViewModel
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
    public double ProfitFactor { get; set; }
    public List<PositionViewModel> RecentTrades { get; set; } = new();
}