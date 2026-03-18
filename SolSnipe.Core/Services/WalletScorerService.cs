using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;

public class WalletScorerService : IWalletScorer
{
    private readonly HttpClient _heliusHttp;
    private readonly HttpClient _dexHttp;
    private readonly WalletScorerConfig _config;
    private readonly HeliusConfig _helius;
    private readonly ILogger<WalletScorerService> _logger;

    private const string HeliusEnhancedTx = "https://api.helius.xyz/v0/addresses/{0}/transactions";
    private const string SOL_MINT = "So11111111111111111111111111111111111111112";

    public WalletScorerService(
        IHttpClientFactory factory,
        IOptions<WalletScorerConfig> scorerConfig,
        IOptions<HeliusConfig> heliusConfig,
        ILogger<WalletScorerService> logger)
    {
        _heliusHttp = factory.CreateClient("helius");
        _dexHttp = factory.CreateClient("dexscreener");
        _config = scorerConfig.Value;
        _helius = heliusConfig.Value;
        _logger = logger;
    }

    private bool HasHeliusKey =>
        !string.IsNullOrEmpty(_helius.ApiKey) && !_helius.ApiKey.Contains("YOUR_");

   
    public async Task<WalletScore> ScoreWalletAsync(string address)
    {
        if (!HasHeliusKey)
            return new WalletScore { Address = address, TotalScore = 0 };

        var txData = await FetchSwapDataAsync(address);
        if (txData.TotalSwaps == 0)
            return new WalletScore { Address = address, TotalScore = 0 };

        if (txData.TotalSwaps < _config.MinTradesForScoring)
        {
            _logger.LogDebug("Wallet {Addr} only {Count} swaps - below min {Min}",
                address[..8] + "...", txData.TotalSwaps, _config.MinTradesForScoring);
            return new WalletScore { Address = address, TotalScore = 0 };
        }

        return CalculateBehavioralScore(address, txData);
    }

    public async Task<List<WalletScore>> ScoreAllCandidatesAsync(
        List<CandidateWallet> candidates, CancellationToken ct)
    {
        if (!HasHeliusKey)
        {
            _logger.LogWarning("No Helius key - cannot score wallets");
            return new List<WalletScore>();
        }

        _logger.LogInformation("Scoring {Total} wallets...", candidates.Count);
        var scores = new List<WalletScore>();
        var done = 0;

        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var score = await ScoreWalletAsync(candidate.Address);
                if (score.TotalScore > 0)
                    scores.Add(score);

                done++;
                if (done % 5 == 0)
                    _logger.LogInformation("Scored {Done}/{Total} | {Count} qualified so far",
                        done, candidates.Count, scores.Count);

                await Task.Delay(1200, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Skipped {Addr}: {Err}", candidate.Address[..8] + "...", ex.Message);
            }
        }

        _logger.LogInformation("Scoring complete - {Count}/{Total} wallets qualified", scores.Count, candidates.Count);

        if (scores.Count > 0)
        {
            var top = scores.OrderByDescending(s => s.TotalScore).First();
            _logger.LogInformation("Top wallet: {Addr} | Score: {Score:F0} | {Swaps} swaps | {Tokens} tokens | {Vol:F1} SOL vol",
                top.Address[..8] + "...", top.TotalScore, top.TotalTrades,
                (int)(top.WinRatePct), top.AvgProfitOnWinPct);
        }

        return scores.OrderByDescending(s => s.TotalScore).ToList();
    }

    
    private record WalletSwapData(
        int TotalSwaps,
        int UniqueTokens,
        double TotalSolVolume,
        DateTime LastTradeAt,
        int BuyCount,
        int SellCount
    );

    private async Task<WalletSwapData> FetchSwapDataAsync(string address)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_config.LookbackDays);
        var tokensSeen = new HashSet<string>();
        var totalSwaps = 0;
        var totalSolVol = 0.0;
        var lastTradeAt = DateTime.MinValue;
        var buyCount = 0;
        var sellCount = 0;
        string? before = null;
        int pages = 0;
        const int maxPg = 3;

        while (pages < maxPg)
        {
            pages++;
            try
            {
                var url = string.Format(HeliusEnhancedTx, address) +
                          $"?api-key={_helius.ApiKey}&type=SWAP&limit=100" +
                          (before != null ? $"&before={before}" : "");

                string resp;
                int retries = 0;
                while (true)
                {
                    var http = await _heliusHttp.GetAsync(url);
                    if (http.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (++retries > 4) goto done;
                        await Task.Delay(retries * 3000);
                        continue;
                    }
                    http.EnsureSuccessStatusCode();
                    resp = await http.Content.ReadAsStringAsync();
                    break;
                }

                var txns = JsonDocument.Parse(resp).RootElement;
                if (txns.ValueKind != JsonValueKind.Array || txns.GetArrayLength() == 0)
                    break;

                bool reachedCutoff = false;

                foreach (var tx in txns.EnumerateArray())
                {
                    // Timestamp
                    if (!tx.TryGetProperty("timestamp", out var tsProp)) continue;
                    var tradeTime = DateTimeOffset.FromUnixTimeSeconds(tsProp.GetInt64()).UtcDateTime;

                    if (tradeTime < cutoff) { reachedCutoff = true; break; }
                    if (tradeTime > lastTradeAt) lastTradeAt = tradeTime;

                    totalSwaps++;

                    // Extract token mints and SOL volume from tokenTransfers
                    if (tx.TryGetProperty("tokenTransfers", out var transfers))
                    {
                        bool isBuy = false;
                        foreach (var t in transfers.EnumerateArray())
                        {
                            if (!t.TryGetProperty("mint", out var mintProp)) continue;
                            var mint = mintProp.GetString() ?? "";
                            if (mint == SOL_MINT || string.IsNullOrEmpty(mint)) continue;
                            tokensSeen.Add(mint);

                            // If toUserAccount == feePayer, this is a token being received (buy)
                            if (tx.TryGetProperty("feePayer", out var fp) &&
                                t.TryGetProperty("toUserAccount", out var toUser) &&
                                toUser.GetString() == fp.GetString())
                                isBuy = true;
                        }
                        if (isBuy) buyCount++; else sellCount++;
                    }

                    // SOL volume from nativeTransfers
                    if (tx.TryGetProperty("nativeTransfers", out var native))
                    {
                        foreach (var n in native.EnumerateArray())
                        {
                            if (n.TryGetProperty("amount", out var amt))
                            {
                                var lamports = amt.ValueKind == JsonValueKind.Number
                                    ? amt.GetInt64()
                                    : long.TryParse(amt.GetString(), out var l) ? l : 0;
                                totalSolVol += lamports / 1_000_000_000.0;
                            }
                        }
                    }
                }

                if (reachedCutoff) break;

                var last = txns[txns.GetArrayLength() - 1];
                if (last.TryGetProperty("signature", out var sig))
                    before = sig.GetString();
                else break;

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error fetching {Addr}: {Err}", address[..8] + "...", ex.Message);
                break;
            }
        }
    done:

        return new WalletSwapData(
            TotalSwaps: totalSwaps,
            UniqueTokens: tokensSeen.Count,
            TotalSolVolume: totalSolVol / 2, // div by 2 as each tx counts both sides
            LastTradeAt: lastTradeAt,
            BuyCount: buyCount,
            SellCount: sellCount
        );
    }

    
    private WalletScore CalculateBehavioralScore(string address, WalletSwapData d)
    {
        // 1. Trade frequency (30 swaps/month = 100 pts)
        var freqScore = Math.Min(100, d.TotalSwaps / 30.0 * 100);

        // 2. Token diversity (10 different tokens = 100 pts — shows breadth)
        var divScore = Math.Min(100, d.UniqueTokens / 10.0 * 100);

        // 3. SOL volume (50 SOL/month = 100 pts)
        var volScore = Math.Min(100, d.TotalSolVolume / 50.0 * 100);

        // 4. Recency (traded in last 7 days = 100, last 30 days = 50, older = 10)
        var daysSinceLast = (DateTime.UtcNow - d.LastTradeAt).TotalDays;
        var recencyScore = daysSinceLast <= 7 ? 100
                          : daysSinceLast <= 30 ? 50
                          : 10;

        var totalScore = freqScore * 0.30
                       + divScore * 0.35
                       + volScore * 0.20
                       + recencyScore * 0.15;

        _logger.LogDebug(
            "Scored {Addr}: swaps={S} tokens={T} sol={V:F1} daysAgo={D:F0} -> {Score:F0}",
            address[..8] + "...", d.TotalSwaps, d.UniqueTokens,
            d.TotalSolVolume, daysSinceLast, totalScore);

        return new WalletScore
        {
            Address = address,
            TotalScore = Math.Round(totalScore, 1),
            TotalTrades = d.TotalSwaps,
            WinRatePct = d.UniqueTokens,       // repurpose: unique tokens
            AvgProfitOnWinPct = d.TotalSolVolume,     // repurpose: SOL volume
            AvgLossOnLossPct = daysSinceLast,         // repurpose: days since last trade
            ScoredAt = DateTime.UtcNow,
        };
    }
}