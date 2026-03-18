using Microsoft.Extensions.Logging;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using Microsoft.Extensions.Options;

namespace SolSnipe.Core.Helpers;


public class FilterHelper
{
    private readonly IPriceService _prices;
    private readonly TradingConfig _config;
    private readonly ILogger<FilterHelper> _logger;

    // Known honeypot / rug token mints — extend this list as needed
    private static readonly HashSet<string> KnownBadTokens = new()
    {
        // Add known scam tokens here
    };

    public FilterHelper(
        IPriceService prices,
        IOptions<TradingConfig> config,
        ILogger<FilterHelper> logger)
    {
        _prices = prices;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<(bool IsValid, string Reason)> IsValidTokenAsync(string tokenMint)
    {
        if (KnownBadTokens.Contains(tokenMint))
            return (false, "Known bad token");

        // Check token age
        var createdAt = await _prices.GetTokenCreatedAtAsync(tokenMint);
        if (createdAt.HasValue)
        {
            var ageMinutes = (DateTime.UtcNow - createdAt.Value).TotalMinutes;
            if (ageMinutes < _config.MinTokenAgeMinutes)
                return (false, $"Token too new ({ageMinutes:F0}min < {_config.MinTokenAgeMinutes}min minimum)");
        }

        // Check market cap
        var mcap = await _prices.GetMarketCapUsdAsync(tokenMint);
        if (mcap.HasValue)
        {
            if (mcap.Value < _config.MinMarketCapUsd)
                return (false, $"Market cap too low (${mcap.Value:N0} < ${_config.MinMarketCapUsd:N0})");

            if (mcap.Value > _config.MaxMarketCapUsd)
                return (false, $"Market cap too high — likely pumped (${mcap.Value:N0} > ${_config.MaxMarketCapUsd:N0})");
        }

        // Check liquidity
        var liquidity = await _prices.GetLiquidityUsdAsync(tokenMint);
        if (liquidity.HasValue && liquidity.Value < _config.MinLiquidityUsd)
            return (false, $"Liquidity too low (${liquidity.Value:N0} < ${_config.MinLiquidityUsd:N0})");

        return (true, "OK");
    }
}


public static class WalletFilterHelper
{
   
    public static bool IsBotWallet(int totalTxCount, int daysActive) =>
        daysActive > 0 && (double)totalTxCount / daysActive > 200;

   
    public static bool IsTooNew(DateTime walletCreatedAt, int minDays = 30) =>
        (DateTime.UtcNow - walletCreatedAt).TotalDays < minDays;

    
    public static bool HasEnoughTrades(int tradeCount, int minTrades = 20) =>
        tradeCount >= minTrades;
}