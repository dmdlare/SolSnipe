// ─── SignalR connection ────────────────────────────────────
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hub/dashboard")
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build();

// ─── Live signal updates ────────────────────────────────────
connection.on("SignalUpdate", (signal) => {
    updateTicker(`Signal: ${signal.tokenSymbol} — ${signal.buyerCount}/${signal.totalWallets} wallets (${signal.signalStrengthPct.toFixed(0)}%)`);
});

// ─── Position updates ──────────────────────────────────────
connection.on("PositionUpdate", (position) => {
    updateTicker(`Position opened: ${position.tokenSymbol} @ $${position.entryPriceUsd.toFixed(8)}`);
});

connection.on("PositionClosed", (position) => {
    const sign = (position.pnlPct ?? 0) >= 0 ? "+" : "";
    updateTicker(`Position closed: ${position.tokenSymbol} ${sign}${position.pnlPct?.toFixed(1)}% / $${position.pnlUsd?.toFixed(2)}`);
    // Flash the row if it's on this page
    const row = document.getElementById("pos-" + position.id);
    if (row) {
        row.style.transition = "opacity 1s";
        row.style.opacity = "0.3";
        setTimeout(() => row.remove(), 1200);
    }
});

// ─── Price updates ─────────────────────────────────────────
connection.on("PriceUpdate", ({ tokenMint, price, pnlPct }) => {
    const priceEl = document.getElementById("price-" + tokenMint);
    const pnlEl = document.getElementById("pnl-" + tokenMint);
    if (priceEl) priceEl.textContent = "$" + price.toFixed(8);
    if (pnlEl) {
        const sign = pnlPct >= 0 ? "+" : "";
        pnlEl.textContent = sign + pnlPct.toFixed(1) + "%";
        pnlEl.className = "mono " + (pnlPct >= 0 ? "positive" : "negative");
    }
});

// ─── Balance update (paper trading) ───────────────────────
connection.on("BalanceUpdate", ({ virtualBalance }) => {
    const el = document.getElementById("virtual-balance");
    if (el) el.textContent = virtualBalance.toFixed(4) + " SOL";
});

// ─── Bot status change ─────────────────────────────────────
connection.on("BotStatusChange", ({ running, paperTrading }) => {
    const dot = document.getElementById("bot-status-dot");
    const text = document.getElementById("bot-status-text");
    if (dot) { dot.className = "status-dot" + (running ? " active" : ""); }
    if (text) { text.textContent = running ? "LIVE" : "OFFLINE"; }
    updateTicker(running ? "Bot started" : "Bot stopped");
});

// ─── Log messages ──────────────────────────────────────────
connection.on("LogMessage", ({ level, message, time }) => {
    updateTicker(`[${time}] ${message}`);
});

// ─── Connection lifecycle ──────────────────────────────────
connection.onreconnecting(() => {
    updateTicker("Reconnecting to bot...");
});
connection.onreconnected(() => {
    updateTicker("Reconnected ✓");
});
connection.onclose(() => {
    updateTicker("Disconnected from bot");
    const dot = document.getElementById("bot-status-dot");
    if (dot) dot.classList.remove("active");
});

// ─── Start connection ──────────────────────────────────────
connection.start()
    .then(() => updateTicker("Connected to SolSnipe ✓"))
    .catch(err => updateTicker("Could not connect to bot: " + err));

// ─── Ticker helper ─────────────────────────────────────────
const tickerMessages = [];
let tickerIndex = 0;

function updateTicker(msg) {
    tickerMessages.push(msg);
    if (tickerMessages.length > 50) tickerMessages.shift();
    const el = document.getElementById("ticker-content");
    if (el) el.textContent = msg;
}

// Cycle through last messages every 8s
setInterval(() => {
    if (tickerMessages.length === 0) return;
    tickerIndex = (tickerIndex + 1) % tickerMessages.length;
    const el = document.getElementById("ticker-content");
    if (el) el.textContent = tickerMessages[tickerIndex];
}, 8000);