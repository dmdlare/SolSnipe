using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;


public class PaperTradeExecutorService : ITradeExecutor
{
    private readonly HttpClient _http;
    private readonly TradingConfig _config;
    private readonly ILogger<PaperTradeExecutorService> _logger;

    // Virtual wallet state
    private double _virtualSolBalance;
    private readonly object _balanceLock = new();

    // Simulated slippage: apply a small realistic penalty to quotes
    // Real trades will have similar slippage so this keeps numbers honest
    private const double SimulatedSlippageFactor = 0.995; // 0.5% simulated slippage

    private const string JupiterQuoteUrl = "https://quote-api.jup.ag/v6/quote";
    private const string SOL_MINT = "So11111111111111111111111111111111111111112";

    public PaperTradeExecutorService(
        IHttpClientFactory factory,
        IOptions<TradingConfig> config,
        ILogger<PaperTradeExecutorService> logger)
    {
        _http = factory.CreateClient("jupiter");
        _config = config.Value;
        _logger = logger;
        _virtualSolBalance = config.Value.PaperTradingStartingBalanceSol;

        _logger.LogDebug("PaperTradeExecutorService initialized. Virtual balance: {Bal} SOL", _virtualSolBalance);
    }

   
    // Simulated Buy: SOL → Token
    
    public async Task<TradeResult> BuyTokenAsync(string tokenMint, double amountSol, int slippageBps)
    {
        lock (_balanceLock)
        {
            if (_virtualSolBalance < amountSol)
            {
                _logger.LogWarning("[PAPER] Insufficient virtual balance: {Bal:F4} SOL < {Need:F4} SOL",
                    _virtualSolBalance, amountSol);
                return Fail("Insufficient virtual SOL balance");
            }
        }

        try
        {
            // Get a REAL quote from Jupiter
            var amountLamports = (long)(amountSol * 1_000_000_000);
            var quote = await GetQuoteAsync(SOL_MINT, tokenMint, amountLamports, slippageBps);
            if (quote is null)
                return Fail("[PAPER] Could not get Jupiter quote for price simulation");

            var rawOutAmount = long.Parse(quote.Value.GetProperty("outAmount").GetString()!);
            var tokenDecimals = GetTokenDecimals(quote.Value);

            // Apply simulated slippage to be realistic
            var tokenAmountReceived = (rawOutAmount / Math.Pow(10, tokenDecimals)) * SimulatedSlippageFactor;
            var pricePerToken = amountSol / tokenAmountReceived;

            // Deduct virtual balance
            lock (_balanceLock)
            {
                _virtualSolBalance -= amountSol;
            }

            var fakeTxId = $"PAPER_{Guid.NewGuid():N}";

            _logger.LogInformation("[PAPER]    BUY SIMULATED");
            _logger.LogInformation("[PAPER]    Token:    {Mint}", tokenMint[..8] + "...");
            _logger.LogInformation("[PAPER]    Spent:    {Sol} SOL", amountSol);
            _logger.LogInformation("[PAPER]    Received: {Amt:F4} tokens", tokenAmountReceived);
            _logger.LogInformation("[PAPER]    Price:    ${Price:F8} per token", pricePerToken);
            _logger.LogInformation("[PAPER]    Balance:  {Bal:F4} SOL remaining", _virtualSolBalance);

            return new TradeResult
            {
                Success = true,
                TxSignature = fakeTxId,
                TokenAmount = tokenAmountReceived,
                ExecutedPriceUsd = pricePerToken,
                ExecutedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError("[PAPER] Buy simulation failed: {Err}", ex.Message);
            return Fail(ex.Message);
        }
    }

    
    // Simulated Sell: Token → SOL
    
    public async Task<TradeResult> SellTokenAsync(string tokenMint, double tokenAmount, int slippageBps)
    {
        try
        {
            // Get a REAL sell quote from Jupiter
            var amountRaw = (long)(tokenAmount * 1_000_000); // rough base units
            var quote = await GetQuoteAsync(tokenMint, SOL_MINT, amountRaw, slippageBps);

            double solReceived;

            if (quote is not null &&
                quote.Value.TryGetProperty("outAmount", out var outProp) &&
                long.TryParse(outProp.GetString(), out var rawOut))
            {
                solReceived = (rawOut / 1_000_000_000.0) * SimulatedSlippageFactor;
            }
            else
            {
                // Fallback: couldn't get quote (token may be illiquid) — use 0
                _logger.LogWarning("[PAPER] Could not get sell quote for {Mint} — recording as 0 SOL received",
                    tokenMint[..8] + "...");
                solReceived = 0;
            }

            // Credit virtual balance
            lock (_balanceLock)
            {
                _virtualSolBalance += solReceived;
            }

            var fakeTxId = $"PAPER_{Guid.NewGuid():N}";

            _logger.LogInformation("[PAPER]    SELL SIMULATED");
            _logger.LogInformation("[PAPER]    Token:    {Mint}", tokenMint[..8] + "...");
            _logger.LogInformation("[PAPER]    Sold:     {Amt:F4} tokens", tokenAmount);
            _logger.LogInformation("[PAPER]    Received: {Sol:F4} SOL", solReceived);
            _logger.LogInformation("[PAPER]    Balance:  {Bal:F4} SOL total", _virtualSolBalance);

            return new TradeResult
            {
                Success = true,
                TxSignature = fakeTxId,
                TokenAmount = solReceived,
                ExecutedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError("[PAPER] Sell simulation failed: {Err}", ex.Message);
            return Fail(ex.Message);
        }
    }

    public async Task<double?> GetQuotedOutputAsync(string inputMint, string outputMint, double inputAmount)
    {
        try
        {
            var amountLamports = (long)(inputAmount * 1_000_000_000);
            var quote = await GetQuoteAsync(inputMint, outputMint, amountLamports, _config.SlippageBps);
            if (quote is null) return null;
            if (quote.Value.TryGetProperty("outAmount", out var out_) &&
                long.TryParse(out_.GetString(), out var amount))
                return amount / 1_000_000_000.0;
        }
        catch { }
        return null;
    }

    public double GetVirtualBalance() { lock (_balanceLock) { return _virtualSolBalance; } }

    
   

    private async Task<JsonElement?> GetQuoteAsync(
        string inputMint, string outputMint, long amount, int slippageBps)
    {
        var url = $"{JupiterQuoteUrl}" +
                  $"?inputMint={inputMint}" +
                  $"&outputMint={outputMint}" +
                  $"&amount={amount}" +
                  $"&slippageBps={slippageBps}";

        var resp = await _http.GetStringAsync(url);
        var doc = JsonDocument.Parse(resp);

        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            _logger.LogDebug("[PAPER] Quote error: {Err}", err.GetString());
            return null;
        }

        return doc.RootElement;
    }

    private static int GetTokenDecimals(JsonElement quote)
    {
        // Jupiter returns outputMintDecimals in the quote
        if (quote.TryGetProperty("outputMintDecimals", out var dec))
            return dec.GetInt32();
        return 9; // default Solana decimals
    }

    private static TradeResult Fail(string reason) =>
        new() { Success = false, ErrorMessage = reason };
}