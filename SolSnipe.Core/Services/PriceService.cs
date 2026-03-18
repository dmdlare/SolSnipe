using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolSnipe.Core.Interfaces;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;


public class PriceService : IPriceService
{
    private readonly HttpClient _http;
    private readonly ILogger<PriceService> _logger;

    private const string JupiterPriceUrl = "https://price.jup.ag/v6/price?ids=";
    private const string DexScreenerUrl = "https://api.dexscreener.com/latest/dex/tokens/";
    private const string SOL_MINT = "So11111111111111111111111111111111111111112";

    public PriceService(IHttpClientFactory factory, ILogger<PriceService> logger)
    {
        _http = factory.CreateClient("price");
        _logger = logger;
    }

    public async Task<double?> GetTokenPriceUsdAsync(string tokenMint)
    {
        try
        {
            // Try Jupiter first — fastest
            var url = $"{JupiterPriceUrl}{tokenMint}&vsToken=USDC";
            var resp = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(resp);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty(tokenMint, out var tokenData) &&
                tokenData.TryGetProperty("price", out var price))
            {
                return price.GetDouble();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Jupiter price failed for {Mint}, falling back to DexScreener: {Err}", tokenMint, ex.Message);
        }

        // Fallback to DexScreener
        return await GetFromDexScreener(tokenMint, d => ExtractPrice(d));
    }

    public async Task<double?> GetMarketCapUsdAsync(string tokenMint)
    {
        return await GetFromDexScreener(tokenMint, d => ExtractMarketCap(d));
    }

    public async Task<double?> GetLiquidityUsdAsync(string tokenMint)
    {
        return await GetFromDexScreener(tokenMint, d => ExtractLiquidity(d));
    }

    public async Task<DateTime?> GetTokenCreatedAtAsync(string tokenMint)
    {
        return await GetFromDexScreener(tokenMint, d => ExtractCreatedAt(d));
    }

    

    private async Task<T?> GetFromDexScreener<T>(string tokenMint, Func<JsonDocument, T?> extractor)
    {
        try
        {
            var resp = await _http.GetStringAsync($"{DexScreenerUrl}{tokenMint}");
            var doc = JsonDocument.Parse(resp);
            return extractor(doc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DexScreener request failed for {Mint}: {Err}", tokenMint, ex.Message);
            return default;
        }
    }

    private static double? ExtractPrice(JsonDocument doc)
    {
        var pair = GetFirstSolanaPair(doc);
        if (pair is null) return null;
        if (pair.Value.TryGetProperty("priceUsd", out var p) &&
            double.TryParse(p.GetString(), out var price))
            return price;
        return null;
    }

    private static double? ExtractMarketCap(JsonDocument doc)
    {
        var pair = GetFirstSolanaPair(doc);
        if (pair is null) return null;
        if (pair.Value.TryGetProperty("marketCap", out var mc))
            return mc.ValueKind == JsonValueKind.Number ? mc.GetDouble() : null;
        return null;
    }

    private static double? ExtractLiquidity(JsonDocument doc)
    {
        var pair = GetFirstSolanaPair(doc);
        if (pair is null) return null;
        if (pair.Value.TryGetProperty("liquidity", out var liq) &&
            liq.TryGetProperty("usd", out var usd))
            return usd.ValueKind == JsonValueKind.Number ? usd.GetDouble() : null;
        return null;
    }

    private static DateTime? ExtractCreatedAt(JsonDocument doc)
    {
        var pair = GetFirstSolanaPair(doc);
        if (pair is null) return null;
        if (pair.Value.TryGetProperty("pairCreatedAt", out var ts) &&
            ts.ValueKind == JsonValueKind.Number)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).UtcDateTime;
        }
        return null;
    }

    private static JsonElement? GetFirstSolanaPair(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("pairs", out var pairs)) return null;
        if (pairs.ValueKind != JsonValueKind.Array) return null;

        foreach (var pair in pairs.EnumerateArray())
        {
            if (pair.TryGetProperty("chainId", out var chain) &&
                chain.GetString() == "solana")
                return pair;
        }
        return null;
    }
}