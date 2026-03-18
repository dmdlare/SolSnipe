using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;

/// <summary>
/// Tracks which wallets have bought which tokens.
///
/// TriggerThresholdPct works as follows:
///   - 1   = trigger on ANY single tracked wallet buying (fastest, most signals)
///   - 50  = trigger when half the tracked wallets have bought
///   - 100 = trigger only when ALL wallets have bought (most conservative)
///
/// For meme coin trading, 1 is recommended — speed matters more than consensus.
/// </summary>
public class SignalAggregatorService : ISignalAggregator
{
    private readonly Dictionary<string, TokenSignal> _signals = new();
    private readonly object _lock = new();
    private readonly TradingConfig _config;
    private readonly ILogger<SignalAggregatorService> _logger;
    private int _totalTrackedWallets;

    public event Func<TokenSignal, Task>? OnThresholdReached;

    public SignalAggregatorService(
        IOptions<TradingConfig> config,
        ILogger<SignalAggregatorService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public void SetTrackedWalletCount(int count) => _totalTrackedWallets = count;

    public void RecordBuy(string walletAddress, string tokenMint)
    {
        TokenSignal signal;
        bool thresholdJustMet = false;

        lock (_lock)
        {
            if (!_signals.TryGetValue(tokenMint, out signal!))
            {
                signal = new TokenSignal
                {
                    TokenMint = tokenMint,
                    TotalTrackedWallets = _totalTrackedWallets,
                    FirstSeenAt = DateTime.UtcNow,
                };
                _signals[tokenMint] = signal;
            }

            // Ignore duplicate buys from same wallet within window
            if (signal.BuyerWallets.Contains(walletAddress))
                return;

            // Reset if signal window has expired
            var windowStart = DateTime.UtcNow.AddMinutes(-_config.SignalWindowMinutes);
            if (signal.FirstSeenAt < windowStart)
            {
                signal.BuyerWallets.Clear();
                signal.FirstSeenAt = DateTime.UtcNow;
                signal.TriggeredBuy = false;
            }

            signal.BuyerWallets.Add(walletAddress);
            signal.TotalTrackedWallets = _totalTrackedWallets;
            signal.LastUpdatedAt = DateTime.UtcNow;

            // Work out if threshold is met.
            // Special case: threshold of 1 means "any single wallet" — fire immediately.
            bool thresholdMet = _config.TriggerThresholdPct <= 1
                ? signal.BuyerWallets.Count >= 1
                : signal.SignalStrengthPct >= _config.TriggerThresholdPct;

            var walletCountStr = _totalTrackedWallets > 0
                ? $"{signal.BuyerWallets.Count}/{signal.TotalTrackedWallets}"
                : $"{signal.BuyerWallets.Count} wallet(s)";

            _logger.LogInformation(
                "Signal: {Mint}... | {Count} | {Pct:F0}% | threshold: {Threshold}%",
                tokenMint[..Math.Min(8, tokenMint.Length)],
                walletCountStr,
                signal.SignalStrengthPct,
                _config.TriggerThresholdPct);

            if (!signal.TriggeredBuy && thresholdMet)
            {
                signal.TriggeredBuy = true;
                thresholdJustMet = true;
            }
        }

        if (thresholdJustMet && OnThresholdReached is not null)
        {
            _logger.LogInformation(
                "[SIGNAL] THRESHOLD MET: {Mint}... bought by {Count} tracked wallet(s)",
                tokenMint[..Math.Min(8, tokenMint.Length)],
                signal.BuyerWallets.Count);

            _ = Task.Run(() => OnThresholdReached(signal));
        }
    }

    public TokenSignal? GetSignal(string tokenMint)
    {
        lock (_lock) { return _signals.GetValueOrDefault(tokenMint); }
    }

    public List<TokenSignal> GetAllSignals()
    {
        lock (_lock) { return _signals.Values.ToList(); }
    }

    public void PruneOldSignals(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        lock (_lock)
        {
            var stale = _signals
                .Where(kv => kv.Value.LastUpdatedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale)
                _signals.Remove(key);

            if (stale.Count > 0)
                _logger.LogDebug("Pruned {Count} stale signals", stale.Count);
        }
    }
}