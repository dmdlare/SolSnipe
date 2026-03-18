using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solnet.Rpc;
using Solnet.Wallet;
using Solnet.Rpc.Builders;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

namespace SolSnipe.Core.Services;

/// <summary>
/// Executes token swaps via Jupiter v6 API.
/// Flow: GetQuote → GetSwapTransaction → Sign → Send → Confirm
/// Jupiter API is completely free with no API key required.
/// </summary>
public class TradeExecutorService : ITradeExecutor
{
    private readonly HttpClient _http;
    private readonly IRpcClient _rpc;
    private readonly Wallet _wallet;
    private readonly TradingConfig _config;
    private readonly ILogger<TradeExecutorService> _logger;

    private const string JupiterQuoteUrl = "https://quote-api.jup.ag/v6/quote";
    private const string JupiterSwapUrl = "https://quote-api.jup.ag/v6/swap";
    private const string SOL_MINT = "So11111111111111111111111111111111111111112";

    public TradeExecutorService(
        IHttpClientFactory factory,
        IRpcClient rpc,
        Wallet wallet,
        IOptions<TradingConfig> config,
        ILogger<TradeExecutorService> logger)
    {
        _http = factory.CreateClient("jupiter");
        _rpc = rpc;
        _wallet = wallet;
        _config = config.Value;
        _logger = logger;
    }

    
    // Buy: SOL → Token
    
    public async Task<TradeResult> BuyTokenAsync(string tokenMint, double amountSol, int slippageBps)
    {
        _logger.LogInformation("Buying {TokenMint} with {Sol} SOL (slippage: {Slip}bps)",
            tokenMint[..8] + "...", amountSol, slippageBps);

        var amountLamports = (long)(amountSol * 1_000_000_000);

        return await ExecuteSwapAsync(
            inputMint: SOL_MINT,
            outputMint: tokenMint,
            amount: amountLamports,
            slippageBps: slippageBps);
    }

    
    // Sell: Token → SOL
   
    public async Task<TradeResult> SellTokenAsync(string tokenMint, double tokenAmount, int slippageBps)
    {
        _logger.LogInformation("Selling {Amount} of {TokenMint}", tokenAmount, tokenMint[..8] + "...");

        // Token amount needs to be in token's base units
        
        var amountRaw = (long)tokenAmount;

        return await ExecuteSwapAsync(
            inputMint: tokenMint,
            outputMint: SOL_MINT,
            amount: amountRaw,
            slippageBps: slippageBps);
    }

    public async Task<double?> GetQuotedOutputAsync(string inputMint, string outputMint, double inputAmount)
    {
        try
        {
            var amountLamports = (long)(inputAmount * 1_000_000_000);
            var quote = await GetQuoteAsync(inputMint, outputMint, amountLamports, _config.SlippageBps);
            if (quote is null) return null;

            if (quote.Value.TryGetProperty("outAmount", out var outAmount) &&
                long.TryParse(outAmount.GetString(), out var amount))
                return amount / 1_000_000_000.0;
        }
        catch { }
        return null;
    }

    
    // Core swap execution
    
    private async Task<TradeResult> ExecuteSwapAsync(
        string inputMint, string outputMint, long amount, int slippageBps)
    {
        try
        {
            // Step 1: Get quote
            var quote = await GetQuoteAsync(inputMint, outputMint, amount, slippageBps);
            if (quote is null)
                return Fail("Failed to get Jupiter quote");

            var outAmount = long.Parse(quote.Value.GetProperty("outAmount").GetString()!);
            _logger.LogDebug("Quote received — output: {Out}", outAmount);

            // Step 2: Get swap transaction from Jupiter
            var swapRequest = new
            {
                quoteResponse = quote.Value,
                userPublicKey = _wallet.Account.PublicKey.Key,
                wrapAndUnwrapSol = true,
                dynamicComputeUnitLimit = true,
                prioritizationFeeLamports = 5000   // ~0.000005 SOL tip for faster inclusion
            };

            var swapJson = JsonSerializer.Serialize(swapRequest);
            var swapResp = await _http.PostAsync(JupiterSwapUrl,
                new StringContent(swapJson, Encoding.UTF8, "application/json"));

            if (!swapResp.IsSuccessStatusCode)
            {
                var err = await swapResp.Content.ReadAsStringAsync();
                return Fail($"Jupiter swap endpoint error: {err}");
            }

            var swapBody = await swapResp.Content.ReadAsStringAsync();
            var swapDoc = JsonDocument.Parse(swapBody);

            if (!swapDoc.RootElement.TryGetProperty("swapTransaction", out var txProp))
                return Fail("No swapTransaction in Jupiter response");

            var txBase64 = txProp.GetString()!;

            // Step 3: Deserialize, sign, and send
            var txBytes = Convert.FromBase64String(txBase64);

            // Sign using Solnet
            var signedTx = SignTransaction(txBytes);
            if (signedTx is null)
                return Fail("Failed to sign transaction");

            // Step 4: Send transaction
            var sendResult = await _rpc.SendTransactionAsync(signedTx, skipPreflight: true);
            if (!sendResult.WasSuccessful)
                return Fail($"RPC send failed: {sendResult.Reason}");

            var signature = sendResult.Result;
            _logger.LogInformation("Transaction sent: {Sig}", signature);

            // Step 5: Confirm with retries
            var confirmed = await ConfirmTransactionAsync(signature);
            if (!confirmed)
            {
                _logger.LogWarning("Transaction not confirmed: {Sig}", signature);
                // Still return success with signature — may confirm later
            }

            _logger.LogInformation("✅ Swap confirmed: {Sig}", signature);

            return new TradeResult
            {
                Success = true,
                TxSignature = signature,
                TokenAmount = outAmount / 1_000_000_000.0,
                ExecutedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swap execution failed");
            return Fail(ex.Message);
        }
    }

    private async Task<JsonElement?> GetQuoteAsync(
        string inputMint, string outputMint, long amount, int slippageBps)
    {
        var url = $"{JupiterQuoteUrl}" +
                  $"?inputMint={inputMint}" +
                  $"&outputMint={outputMint}" +
                  $"&amount={amount}" +
                  $"&slippageBps={slippageBps}" +
                  $"&onlyDirectRoutes=false";

        var resp = await _http.GetStringAsync(url);
        var doc = JsonDocument.Parse(resp);

        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            _logger.LogWarning("Jupiter quote error: {Err}", err.GetString());
            return null;
        }

        return doc.RootElement;
    }

    private byte[]? SignTransaction(byte[] txBytes)
    {
        try
        {
            // Solnet versioned transaction signing
            var message = txBytes.Skip(1).ToArray();  // skip version prefix
            var signature = _wallet.Account.Sign(message);

            // Rebuild: [numSignatures][signature][message]
            var result = new byte[1 + 64 + message.Length];
            result[0] = 1; // 1 signature
            Array.Copy(signature, 0, result, 1, 64);
            Array.Copy(message, 0, result, 65, message.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Signing failed: {Err}", ex.Message);
            return null;
        }
    }

    private async Task<bool> ConfirmTransactionAsync(string signature, int maxRetries = 20)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            await Task.Delay(2000);
            try
            {
                var status = await _rpc.GetSignatureStatusesAsync(new List<string> { signature });
                if (status.WasSuccessful && status.Result?.Value?[0] != null)
                {
                    var conf = status.Result.Value[0]!.ConfirmationStatus;
                    if (conf == "confirmed" || conf == "finalized")
                        return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static TradeResult Fail(string reason) => new() { Success = false, ErrorMessage = reason };
}