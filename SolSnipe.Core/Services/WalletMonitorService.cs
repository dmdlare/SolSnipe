using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;

namespace SolSnipe.Core.Services;


public class WalletMonitorService : IWalletMonitor, IAsyncDisposable
{
    public event Func<string, string, string, Task>? OnWalletBuy;

    private readonly SolanaConfig _solana;
    private readonly ILogger<WalletMonitorService> _logger;

    private readonly Dictionary<string, ClientWebSocket> _sockets = new();
    private readonly Dictionary<string, CancellationTokenSource> _socketCts = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1); // one connection at a time
    private CancellationToken _globalCt;

    private static readonly HashSet<string> SwapPrograms = new()
    {
        "JUP6LkbZbjS1jKKwapdHNy74zcZ3tLUZoi5QNyVTaV4",  // Jupiter v6
        "JUP4Fb2cqiRUcaTHdrPC8h2gNsA2ETXiPDD33WcGuJB",  // Jupiter v4
        "675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8", // Raydium AMM
        "CAMMCzo5YL8w4VFF8KVHrK22GGUsp5VTaW7grrKgrWqK", // Raydium CLMM
        "whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc",  // Orca Whirlpool
    };

    public WalletMonitorService(
        IOptions<SolanaConfig> solana,
        ILogger<WalletMonitorService> logger)
    {
        _solana = solana.Value;
        _logger = logger;
    }

    public async Task StartAsync(IEnumerable<string> walletAddresses, CancellationToken ct)
    {
        _globalCt = ct;

        if (string.IsNullOrEmpty(_solana.WsUrl) || _solana.WsUrl.Contains("YOUR_HELIUS"))
        {
            _logger.LogWarning("WebSocket URL not configured - wallet monitoring disabled.");
            _logger.LogWarning("Set Solana:WsUrl in appsettings.json to enable live monitoring.");
            return;
        }

        var addresses = walletAddresses.ToList();
        _logger.LogInformation("Starting wallet monitor for {Count} wallets (staggered 2s apart)", addresses.Count);

        // Stagger connections — 2 seconds between each wallet
        // This prevents the 429 burst that happens when all connect simultaneously
        _ = Task.Run(async () =>
        {
            foreach (var address in addresses)
            {
                if (ct.IsCancellationRequested) break;
                await SubscribeToWalletAsync(address);
                await Task.Delay(2000, ct); // 2s stagger between connections
            }
        }, ct);
    }

    public async Task StopAsync()
    {
        foreach (var cts in _socketCts.Values)
            cts.Cancel();

        foreach (var ws in _sockets.Values)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
                ws.Dispose();
            }
            catch { }
        }

        _sockets.Clear();
        _socketCts.Clear();
        _logger.LogInformation("Wallet monitor stopped");
    }

    public HashSet<string> GetMonitoredAddresses()
    {
        return _sockets.Keys.ToHashSet();
    }

    public async Task AddWalletAsync(string address)
    {
        if (_sockets.ContainsKey(address)) return;
        await Task.Delay(2000); // stagger new additions too
        await SubscribeToWalletAsync(address);
        _logger.LogInformation("Added wallet to monitor: {Address}", address[..8] + "...");
    }

    public async Task RemoveWalletAsync(string address)
    {
        if (_socketCts.TryGetValue(address, out var cts))
        {
            cts.Cancel();
            _socketCts.Remove(address);
        }
        if (_sockets.TryGetValue(address, out var ws))
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Removed", CancellationToken.None);
                ws.Dispose();
            }
            catch { }
            _sockets.Remove(address);
        }
    }

    private async Task SubscribeToWalletAsync(string walletAddress)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_globalCt);
        var ws = new ClientWebSocket();
        _sockets[walletAddress] = ws;
        _socketCts[walletAddress] = cts;

        _ = Task.Run(() => ListenLoopAsync(walletAddress, ws, cts.Token), cts.Token);
        await Task.CompletedTask;
    }

    private async Task ListenLoopAsync(string walletAddress, ClientWebSocket ws, CancellationToken ct)
    {
        int reconnectDelay = 5000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Connecting WebSocket for {Wallet}", walletAddress[..8] + "...");

                // Create a fresh socket if needed
                if (ws.State != WebSocketState.None && ws.State != WebSocketState.Connecting && ws.State != WebSocketState.Open)
                {
                    ws.Dispose();
                    ws = new ClientWebSocket();
                    _sockets[walletAddress] = ws;
                }

                await ws.ConnectAsync(new Uri(_solana.WsUrl), ct);

                // Subscribe to logs mentioning this wallet
                var sub = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "logsSubscribe",
                    @params = new object[]
                    {
                        new { mentions = new[] { walletAddress } },
                        new { commitment = "confirmed" }
                    }
                });

                await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);
                _logger.LogInformation("Subscribed to wallet: {Wallet}", walletAddress[..8] + "...");

                reconnectDelay = 5000; // reset backoff on successful connect

                // Read messages
                var buffer = new byte[65536];
                var sb = new StringBuilder();

                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        await ProcessMessageAsync(walletAddress, sb.ToString());
                        sb.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // On 429, back off longer before retrying
                if (ex.Message.Contains("429"))
                {
                    reconnectDelay = Math.Min(reconnectDelay * 2, 60000); // max 60s
                    _logger.LogDebug("Rate limited on WebSocket for {Wallet} - waiting {Delay}ms",
                        walletAddress[..8] + "...", reconnectDelay);
                }
                else
                {
                    _logger.LogDebug("WebSocket error for {Wallet}: {Err} - reconnecting in {Delay}ms",
                        walletAddress[..8] + "...", ex.Message, reconnectDelay);
                }

                try { ws.Dispose(); } catch { }
                ws = new ClientWebSocket();
                _sockets[walletAddress] = ws;

                await Task.Delay(reconnectDelay, ct);
            }
        }
    }

    private async Task ProcessMessageAsync(string walletAddress, string message)
    {
        try
        {
            var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Skip subscription confirmations
            if (root.TryGetProperty("id", out _) && !root.TryGetProperty("method", out _))
                return;

            if (!root.TryGetProperty("method", out var method) ||
                method.GetString() != "logsNotification")
                return;

            if (!root.TryGetProperty("params", out var prms) ||
                !prms.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("value", out var value))
                return;

            if (!value.TryGetProperty("signature", out var sigProp)) return;
            var signature = sigProp.GetString() ?? string.Empty;

            if (!value.TryGetProperty("logs", out var logs)) return;

            var logLines = logs.EnumerateArray()
                               .Select(l => l.GetString() ?? string.Empty)
                               .ToList();

            // Check if this is a swap transaction
            bool isSwap = logLines.Any(line =>
                SwapPrograms.Any(prog => line.Contains(prog)));

            if (!isSwap) return;

            var tokenMint = ExtractOutputToken(logLines);
            if (string.IsNullOrEmpty(tokenMint)) return;

            // Skip SOL (that would be a sell)
            if (tokenMint == "So11111111111111111111111111111111111111112") return;

            _logger.LogInformation(
                "[SWAP] {Wallet} bought {Token}... | tx: {Sig}",
                walletAddress[..8] + "...",
                tokenMint[..8],
                signature[..Math.Min(16, signature.Length)] + "...");

            if (OnWalletBuy is not null)
                await OnWalletBuy(walletAddress, tokenMint, signature);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error processing WS message: {Err}", ex.Message);
        }
    }

    private static string ExtractOutputToken(List<string> logs)
    {
        foreach (var log in logs)
        {
            if (!log.Contains("Transfer")) continue;
            var parts = log.Split(' ');
            foreach (var part in parts)
            {
                if (part.Length is >= 32 and <= 44 && IsBase58(part))
                    return part;
            }
        }
        return string.Empty;
    }

    private static bool IsBase58(string s) =>
        s.All(c => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c));

    public async ValueTask DisposeAsync() => await StopAsync();
}