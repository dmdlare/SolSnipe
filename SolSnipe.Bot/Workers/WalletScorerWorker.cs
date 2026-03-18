using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Bot.Workers;

/// <summary>
/// Quartz job that runs weekly to rescore wallets and refresh the tracked list.
/// The core logic lives in RunAsync() so BotWorker can also call it on first launch.
/// Schedule set in appsettings.json: WalletScorer.RescoringCronSchedule
/// </summary>
[DisallowConcurrentExecution]
public class WalletScorerWorker : IJob
{
    private readonly IWalletDiscovery _discovery;
    private readonly IWalletScorer _scorer;
    private readonly IWalletMonitor _monitor;
    private readonly IWalletRepository _wallets;
    private readonly WalletScorerConfig _config;
    private readonly ILogger<WalletScorerWorker> _logger;

    public WalletScorerWorker(
        IWalletDiscovery discovery,
        IWalletScorer scorer,
        IWalletMonitor monitor,
        IWalletRepository wallets,
        IOptions<WalletScorerConfig> config,
        ILogger<WalletScorerWorker> logger)
    {
        _discovery = discovery;
        _scorer = scorer;
        _monitor = monitor;
        _wallets = wallets;
        _config = config.Value;
        _logger = logger;
    }

    // Called by Quartz on weekly schedule
    public async Task Execute(IJobExecutionContext context)
    {
       
        _logger.LogInformation("  Weekly wallet rescore starting...");
       
        await RunAsync(context.CancellationToken);
    }

    // Called directly by BotWorker on first launch when wallet list is empty
    public async Task RunAsync(CancellationToken ct)
    {
        //Discover candidate wallets from DexScreener trending tokens
        _logger.LogInformation("Discovering candidate wallets from trending tokens...");
        var candidates = await _discovery.DiscoverCandidatesAsync(ct);
        _logger.LogInformation("Discovered {Count} candidate wallets", candidates.Count);

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No candidates found — check your internet connection and DexScreener availability");
            return;
        }

        // Skip wallets already tracked
        var currentWallets = _wallets.GetActive();
        var currentAddresses = currentWallets.Select(w => w.Address).ToHashSet();
        var newCandidates = candidates
            .Where(c => !currentAddresses.Contains(c.Address))
            .ToList();

        _logger.LogInformation("{New} new candidates to score (skipping {Existing} already tracked)",
            newCandidates.Count, currentAddresses.Count);

        if (newCandidates.Count == 0)
        {
            _logger.LogInformation("All candidates already tracked — nothing to update");
            return;
        }

        //Score every candidate via Helius trade history
        _logger.LogInformation("Scoring wallets — this takes 5-10 minutes on first run...");
        var scores = await _scorer.ScoreAllCandidatesAsync(newCandidates, ct);
        _logger.LogInformation("Scored {Count} wallets successfully", scores.Count);

        if (scores.Count == 0)
        {
            _logger.LogWarning("No wallets scored - DexScreener top-trader data unavailable.");
            _logger.LogWarning("The bot will still work but is tracking unranked wallets.");
            _logger.LogWarning("Add Helius:ApiKey to appsettings.json for proper scoring.");
            return;
        }

        // Merge with existing, keep top N by score
        var existingScored = currentWallets.Select(w => new WalletScore
        {
            Address = w.Address,
            TotalScore = w.Score,
        }).ToList();

        var allScores = existingScored
            .Concat(scores)
            .GroupBy(s => s.Address)
            .Select(g => g.OrderByDescending(s => s.TotalScore).First())
            .OrderByDescending(s => s.TotalScore)
            .Take(_config.MaxTrackedWallets)
            .ToList();

        var topAddresses = allScores.Select(s => s.Address).ToHashSet();

        // Deactivate wallets that dropped out of top N
        foreach (var wallet in currentWallets.Where(w => !topAddresses.Contains(w.Address)))
        {
            _wallets.Deactivate(wallet.Address);
            await _monitor.RemoveWalletAsync(wallet.Address);
            _logger.LogInformation("Dropped: {Addr} (score: {Score:F1})",
                wallet.Address[..8] + "...", wallet.Score);
        }

        // Save top wallets and start monitoring new ones
        foreach (var score in allScores)
        {
            var isNew = !currentAddresses.Contains(score.Address);

            _wallets.Upsert(new TrackedWallet
            {
                Address = score.Address,
                Label = $"AUTO_{score.Address[..6]}",
                Score = score.TotalScore,
                // WinRatePct repurposed = unique tokens count in behavioral scoring
                // AvgProfitOnWinPct repurposed = SOL volume
                WinRate = score.WinRatePct,     // = unique tokens
                AvgProfitPct = score.AvgProfitOnWinPct, // = SOL volume
                AvgLossPct = score.AvgLossOnLossPct,  // = days since last trade
                TotalTrades = score.TotalTrades,
                LastScored = DateTime.UtcNow,
                IsActive = true,
            });

            if (isNew)
            {
                await _monitor.AddWalletAsync(score.Address);
                _logger.LogInformation(
                    "Now tracking: {Addr} | Score: {Score:F0} | Swaps: {Swaps} | Tokens: {Tokens} | SOL vol: {Vol:F1}",
                    score.Address[..8] + "...",
                    score.TotalScore,
                    score.TotalTrades,
                    (int)score.WinRatePct,
                    score.AvgProfitOnWinPct);
            }
        }

       
        _logger.LogInformation("Wallet list updated — now tracking {Count} wallets", allScores.Count);
        _logger.LogInformation(
            "Top scorer: {Addr} | Score: {Score:F0} | Swaps: {Swaps} | Tokens: {Tokens} | SOL: {Vol:F1}",
            allScores[0].Address[..8] + "...",
            allScores[0].TotalScore,
            allScores[0].TotalTrades,
            (int)allScores[0].WinRatePct,
            allScores[0].AvgProfitOnWinPct);
        
    }
}