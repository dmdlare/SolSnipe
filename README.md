# SolSnipe 

A Solana meme coin copy-trading bot built with .NET 9. Automatically discovers and scores high-activity trader wallets, monitors them via WebSocket, and executes copy trades through Jupiter DEX when they buy tokens.

Includes a real-time ASP.NET Core dashboard with SignalR live updates.

---

## Features

- **Automatic wallet discovery** — finds active traders from DexScreener trending tokens
- **Behavioral wallet scoring** — ranks wallets by swap frequency, token diversity, SOL volume, and recency (0–100 score)
- **Real-time WebSocket monitoring** — subscribes to Solana logs for each tracked wallet via Helius
- **Signal aggregation** — fires when a threshold of tracked wallets buys the same token
- **Jupiter DEX integration** — fetches real quotes and executes swaps (or simulates them in paper mode)
- **Paper trading mode** — simulates all trades with a virtual SOL balance, no real funds used
- **Position management** — automatic take-profit and stop-loss monitoring every 30 seconds
- **Weekly wallet rescoring** — Quartz scheduler re-discovers and re-scores wallets every Sunday
- **Live dashboard** — ASP.NET Core MVC + SignalR showing wallets, signals, open positions, and trade history

---

## Architecture

```
SolSnipe/
├── SolSnipe.Bot/           # Worker service — BotWorker, PositionMonitorWorker, WalletScorerWorker
├── SolSnipe.Core/          # Business logic, interfaces, models, services
├── SolSnipe.Api/           # Helius webhook receiver
├── SolSnipe.Data/          # LiteDB repositories
├── SolSnipe.Dashboard/     # ASP.NET Core MVC dashboard (main runnable project)
└── SolSnipe.Tests/         # xUnit tests
```

The Dashboard is the single entry point — it hosts all Bot workers internally. Run `SolSnipe.Dashboard`, not `SolSnipe.Bot`.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9)
- [Helius API key](https://helius.dev) (free tier works)
- A Solana wallet private key (base58) for live trading — not needed for paper trading

---

## Setup

### 1. Clone the repository

```bash
git clone https://github.com/yourusername/SolSnipe.git
cd SolSnipe
```

### 2. Configure `appsettings.json`

Edit `SolSnipe.Dashboard/appsettings.json`:

```json
{
  "Solana": {
    "RpcUrl": "https://mainnet.helius-rpc.com/?api-key=YOUR_HELIUS_KEY",
    "WsUrl":  "wss://mainnet.helius-rpc.com/?api-key=YOUR_HELIUS_KEY",
    "WalletPrivateKey": "YOUR_BASE58_PRIVATE_KEY"
  },
  "Helius": {
    "ApiKey": "YOUR_HELIUS_KEY"
  },
  "Bot": {
    "PaperTrading": true,
    "PaperTradingStartingBalanceSol": 10.0,
    "TriggerThresholdPct": 1,
    "BuyAmountSol": 0.5,
    "TakeProfitPct": 50,
    "StopLossPct": 20,
    "MaxOpenPositions": 5,
    "MinMarketCapUsd": 10000,
    "MinTokenAgeMinutes": 5
  },
  "WalletScorer": {
    "MaxTrackedWallets": 10,
    "MinTradesForScoring": 5,
    "LookbackDays": 30,
    "TrendingTokensToScan": 5
  },
  "Database": {
    "Path": "data/solsnipe.db"
  }
}
```

> **Never commit your private key or API key.** Add `appsettings.json` to `.gitignore`.

### 3. Run

```bash
cd SolSnipe.Dashboard
dotnet run
```

Or open `SolSnipe.sln` in Visual Studio, set `SolSnipe.Dashboard` as the startup project, and press F5.

Open `http://localhost:5000` in your browser.

---

## First Launch

On first run the bot will:

1. Fetch trending tokens from DexScreener
2. Extract trader wallet addresses from recent swap transactions via Helius
3. Score each wallet on behavioral metrics (takes ~10 minutes for 50 wallets)
4. Subscribe to the top 10 wallets via WebSocket (staggered 2s apart to avoid rate limits)
5. Start monitoring for buy signals

You'll see live progress in the console. Once wallets are tracked, the dashboard populates automatically.

---

## Wallet Scoring

Wallets are scored 0–100 using four behavioral signals pulled from Helius trade history:

| Signal | Weight | Description |
|--------|--------|-------------|
| Swap frequency | 30% | Swaps per month — active traders score higher |
| Token diversity | 35% | Unique tokens traded — breadth shows experience |
| SOL volume | 20% | Total SOL traded — larger positions = more committed |
| Recency | 15% | Days since last trade — still active = 100pts |

Only wallets scoring above the minimum threshold are tracked. The list is refreshed every Sunday via a Quartz scheduler job.

---

## Signal Logic

A signal fires when a tracked wallet buys a token. With `TriggerThresholdPct: 1`, any single wallet buy triggers the bot immediately — optimal for meme coin trading where speed matters.

Before executing a trade, the bot checks:
- Token age (minimum 5 minutes)
- Market cap range ($10k–$50M)
- Minimum liquidity ($50k)
- Maximum open positions not exceeded
- Not already holding this token

---

## Paper Trading

Paper trading is enabled by default (`PaperTrading: true`). In this mode:

- All signals and filters run normally
- Jupiter is called for real price quotes
- Trades are simulated against a virtual SOL balance
- P&L is tracked and visible in the dashboard History page

Run for 1–2 weeks, then check the History page. If profit factor is above 1.5 and win rate is above 40%, consider going live by setting `PaperTrading: false`.

---

## Going Live

1. Create a dedicated Solana wallet (never use your main wallet)
2. Fund it with the amount you want to trade with
3. Set `PaperTrading: false` in `appsettings.json`
4. Set `WalletPrivateKey` to your new wallet's base58 private key
5. Restart the dashboard

> ⚠️ Copy trading meme coins carries significant financial risk. Only trade with funds you can afford to lose entirely.

---

## Dashboard Pages

| Page | Description |
|------|-------------|
| Overview | Virtual balance, open positions with live P&L, recent signals |
| Positions | All open positions with current prices |
| History | Closed trades, win rate, profit factor, cumulative P&L |
| Signals | Recent buy signals with wallet breakdown |
| Wallets | Tracked wallets with scores and trading stats |

---

## Configuration Reference

### Bot settings

| Key | Default | Description |
|-----|---------|-------------|
| `PaperTrading` | `true` | Simulate trades without real funds |
| `TriggerThresholdPct` | `1` | % of wallets that must buy to trigger (1 = any single wallet) |
| `BuyAmountSol` | `0.5` | SOL to spend per trade |
| `TakeProfitPct` | `50` | Close position at +50% |
| `StopLossPct` | `20` | Close position at -20% |
| `MaxOpenPositions` | `5` | Maximum concurrent positions |
| `MinMarketCapUsd` | `10000` | Minimum token market cap |
| `MinTokenAgeMinutes` | `5` | Minimum token age before buying |

### Wallet scorer settings

| Key | Default | Description |
|-----|---------|-------------|
| `MaxTrackedWallets` | `10` | Number of wallets to monitor |
| `MinTradesForScoring` | `5` | Minimum swaps for a wallet to qualify |
| `LookbackDays` | `30` | Days of history to score |
| `TrendingTokensToScan` | `5` | Trending tokens to use for discovery |

---

## Tech Stack

- **.NET 9** / C# 13
- **ASP.NET Core** — dashboard web server
- **SignalR** — real-time dashboard updates
- **LiteDB** — embedded document database (shared mode for multi-process access)
- **Solnet** — Solana .NET SDK
- **Jupiter API** — DEX aggregator for swap quotes and execution
- **Helius** — enhanced Solana transaction API and WebSocket subscriptions
- **DexScreener** — trending token discovery
- **Quartz.NET** — weekly wallet rescoring scheduler
- **Serilog** — structured logging to console and file

---

## Project Status

Currently in active paper trading phase. Core functionality complete:

- [x] Wallet discovery and behavioral scoring
- [x] Real-time WebSocket monitoring
- [x] Signal aggregation and filtering
- [x] Paper trade execution with Jupiter quotes
- [x] Position monitoring (TP/SL)
- [x] Live dashboard with SignalR
- [x] Weekly wallet rescoring
- [ ] Live trade execution (ready, needs paper trading validation first)
- [ ] VPS deployment scripts
- [ ] Telegram notifications

---

## License

MIT
