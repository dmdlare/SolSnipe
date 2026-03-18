using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;


public class WalletDiscoveryService : IWalletDiscovery
{
    private readonly HttpClient _httpDex;
    private readonly HttpClient _httpHelius;
    private readonly WalletScorerConfig _config;
    private readonly HeliusConfig _helius;
    private readonly ILogger<WalletDiscoveryService> _logger;

    private const string DexScreenerTrending = "https://api.dexscreener.com/token-boosts/top/v1";
    private const string HeliusTxUrl = "https://api.helius.xyz/v0/addresses/{0}/transactions";

    public WalletDiscoveryService(
        IHttpClientFactory factory,
        IOptions<WalletScorerConfig> config,
        IOptions<HeliusConfig> heliusConfig,
        ILogger<WalletDiscoveryService> logger)
    {
        _httpDex = factory.CreateClient("dexscreener");
        _httpHelius = factory.CreateClient("helius");
        _config = config.Value;
        _helius = heliusConfig.Value;
        _logger = logger;
    }

    public async Task<List<CandidateWallet>> DiscoverCandidatesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting wallet discovery...");

        // get trending token mints
        var trendingMints = await GetTrendingTokenMintsAsync(ct);
        _logger.LogInformation("Found {Count} trending tokens to scan", trendingMints.Count);

        var candidates = new Dictionary<string, CandidateWallet>();

        // for each token, get recent swap transactions and extract trader wallets
        foreach (var mint in trendingMints.Take(_config.TrendingTokensToScan))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var wallets = await GetTradersForTokenAsync(mint, ct);
                foreach (var w in wallets)
                    if (!candidates.ContainsKey(w.Address))
                        candidates[w.Address] = w;

                _logger.LogDebug("Found {Count} traders for {Mint}", wallets.Count, mint[..8] + "...");
                await Task.Delay(1500, ct); // Helius free tier rate limit
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed getting traders for {Mint}: {Err}", mint[..8] + "...", ex.Message);
            }
        }

        _logger.LogInformation("Discovery complete - {Count} unique candidate wallets found", candidates.Count);
        return candidates.Values.ToList();
    }

    //  trending token mints from DexScreener 
    private async Task<List<string>> GetTrendingTokenMintsAsync(CancellationToken ct)
    {
        var mints = new List<string>();
        try
        {
            var resp = await _httpDex.GetStringAsync(DexScreenerTrending, ct);
            var doc = JsonDocument.Parse(resp);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("tokenAddress", out var addr) &&
                        item.TryGetProperty("chainId", out var chain) &&
                        chain.GetString() == "solana")
                    {
                        var mint = addr.GetString();
                        if (!string.IsNullOrEmpty(mint))
                            mints.Add(mint);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DexScreener trending fetch failed: {Err}", ex.Message);
        }

        return mints.Distinct().ToList();
    }

    //  get actual trader wallet addresses for a token ───────────────
    // Uses Helius to fetch recent swap transactions for the token mint,
    // then extracts the feePayer
    private async Task<List<CandidateWallet>> GetTradersForTokenAsync(
        string tokenMint, CancellationToken ct)
    {
        var traders = new Dictionary<string, CandidateWallet>();

        try
        {
           
            var url = string.Format(HeliusTxUrl, tokenMint) +
                      $"?api-key={_helius.ApiKey}&type=SWAP&limit=100";

            // Retry on 429
            string resp = string.Empty;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var httpResp = await _httpHelius.GetAsync(url, ct);
                if (httpResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var wait = attempt * 3000;
                    _logger.LogDebug("Rate limited (429) on discovery - waiting {Wait}ms", wait);
                    await Task.Delay(wait, ct);
                    continue;
                }
                httpResp.EnsureSuccessStatusCode();
                resp = await httpResp.Content.ReadAsStringAsync(ct);
                break;
            }
            if (string.IsNullOrEmpty(resp)) return traders.Values.ToList();
            var txns = JsonDocument.Parse(resp).RootElement;

            if (txns.ValueKind != JsonValueKind.Array) return traders.Values.ToList();

            foreach (var tx in txns.EnumerateArray())
            {
                // feePayer is the wallet that initiated and paid for the transaction
               
                if (!tx.TryGetProperty("feePayer", out var feePayer)) continue;
                var walletAddr = feePayer.GetString();
                if (string.IsNullOrEmpty(walletAddr)) continue;

                // Skip program addresses (they start with known prefixes or are very short)
                if (walletAddr.Length < 32) continue;

                if (!traders.ContainsKey(walletAddr))
                {
                    traders[walletAddr] = new CandidateWallet
                    {
                        Address = walletAddr,
                        DiscoveredFrom = tokenMint,
                        DiscoveredAt = DateTime.UtcNow,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Helius trader fetch failed for {Mint}: {Err}",
                tokenMint[..8] + "...", ex.Message);
        }

        return traders.Values
            .Take(_config.CandidatesPerTrendingToken)
            .ToList();
    }
}