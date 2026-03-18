using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Asn1.Ocsp;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;

namespace SolSnipe.Api.Controllers;

/// <summary>
/// Receives Helius webhook POST requests when tracked wallets transact.
/// Helius enhanced transactions arrive pre-parsed with token transfer data.
/// Register this URL at: https://dev.helius.xyz/dashboard/app → Webhooks
/// </summary>
[ApiController]
[Route("webhook")]
public class WebhookController : ControllerBase
{
    private readonly ISignalAggregator _signals;
    private readonly HeliusConfig _config;
    private readonly ILogger<WebhookController> _logger;

    private const string SOL_MINT = "So11111111111111111111111111111111111111112";

    public WebhookController(
        ISignalAggregator aggregator,
        IOptions<HeliusConfig> config,
        ILogger<WebhookController> logger)
    {
        _signals = aggregator;
        _config = config.Value;
        _logger = logger;
    }

    [HttpPost("helius")]
    public async Task<IActionResult> HeliusWebhook()
    {
        // Validate Helius signature
        if (!await ValidateSignatureAsync())
        {
            _logger.LogWarning("Invalid Helius webhook signature — rejected");
            return Unauthorized();
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync();

        try
        {
            var events = JsonDocument.Parse(body).RootElement;
            if (events.ValueKind != JsonValueKind.Array) return Ok();

            foreach (var evt in events.EnumerateArray())
                ProcessEvent(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Helius webhook");
        }

        return Ok();
    }

    // -----------------------------------------------------------------------
    // Private
    // -----------------------------------------------------------------------

    private void ProcessEvent(JsonElement evt)
    {
        // Helius enhanced transaction format
        if (!evt.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();

        // We only care about SWAP events
        if (type != "SWAP") return;

        if (!evt.TryGetProperty("feePayer", out var feePayer)) return;
        var walletAddress = feePayer.GetString() ?? string.Empty;

        if (!evt.TryGetProperty("signature", out var sigProp)) return;
        var signature = sigProp.GetString() ?? string.Empty;

        // Extract the token being bought (received, non-SOL side of swap)
        var tokenMint = ExtractBoughtToken(evt);
        if (string.IsNullOrEmpty(tokenMint))
        {
            _logger.LogDebug("Could not extract token from swap event");
            return;
        }

        _logger.LogInformation(
            "Helius webhook: {Wallet} bought {Token} | tx: {Sig}",
            walletAddress[..8] + "...",
            tokenMint[..8] + "...",
            signature[..16] + "...");

        _signals.RecordBuy(walletAddress, tokenMint);
    }

    private static string ExtractBoughtToken(JsonElement evt)
    {
        // Helius enhanced swap events have tokenInputs / tokenOutputs
        if (evt.TryGetProperty("tokenOutputs", out var outputs))
        {
            foreach (var output in outputs.EnumerateArray())
            {
                if (!output.TryGetProperty("mint", out var mint)) continue;
                var mintAddr = mint.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(mintAddr) && mintAddr != SOL_MINT)
                    return mintAddr;
            }
        }

        // Fallback: check tokenTransfers for received tokens
        if (evt.TryGetProperty("tokenTransfers", out var transfers))
        {
            foreach (var transfer in transfers.EnumerateArray())
            {
                if (!transfer.TryGetProperty("mint", out var mint)) continue;
                var mintAddr = mint.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(mintAddr) || mintAddr == SOL_MINT) continue;

                // If toUserAccount matches feePayer, this is a received token
                if (evt.TryGetProperty("feePayer", out var feePayer) &&
                    transfer.TryGetProperty("toUserAccount", out var toUser) &&
                    toUser.GetString() == feePayer.GetString())
                    return mintAddr;
            }
        }

        return string.Empty;
    }

    private async Task<bool> ValidateSignatureAsync()
    {
        if (string.IsNullOrEmpty(_config.WebhookSecret))
            return true; // No secret configured, skip validation

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return false;

        Request.Body.Position = 0;
        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        Request.Body.Position = 0;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        return authHeader.ToString() == expected;
    }
}