using Microsoft.Extensions.Options;
using Serilog;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Core.Services;
using SolSnipe.Dashboard;
using SolSnipe.Dashboard.Hubs;
using SolSnipe.Data;
using SolSnipe.Data.Repositories;
using static SolSnipe.Core.Interfaces.IWalletMonitor;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

// ── MVC + SignalR ─────────────────────────────────────────
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

// ── Config ────────────────────────────────────────────────
builder.Services.Configure<TradingConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<SolanaConfig>(builder.Configuration.GetSection("Solana"));

// ── HTTP clients (only what the dashboard actually needs) ─
// Used by PriceService to show live prices on open positions
builder.Services.AddHttpClient("price", c => { c.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddHttpClient("dexscreener", c =>
{
    c.BaseAddress = new Uri("https://api.dexscreener.com");
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("jupiter", c =>
{
    c.BaseAddress = new Uri("https://quote-api.jup.ag");
    c.Timeout = TimeSpan.FromSeconds(15);
});

// ── Database (read/write — shared with Bot via same .db file)
builder.Services.AddSingleton<SolSnipeDb>();
builder.Services.AddSingleton<IPositionRepository, PositionRepository>();
builder.Services.AddSingleton<IWalletRepository, WalletRepository>();
builder.Services.AddSingleton<ITradeHistoryRepository, TradeHistoryRepository>();

// ── Display services ──────────────────────────────────────
// PriceService: fetches live prices for open position P&L display
builder.Services.AddSingleton<IPriceService, PriceService>();

// SignalAggregator: shared in-memory signal state
// NOTE: if Bot and Dashboard run as separate processes this is empty
// until the Bot pushes signals via SignalR. That's fine.
builder.Services.AddSingleton<ISignalAggregator, SignalAggregatorService>();

// BotStateService: tracks running/paper state shown in navbar
builder.Services.AddSingleton<BotStateService>();

// DashboardNotifier: SignalR broadcaster (Bot calls this to push live events)
builder.Services.AddSingleton<DashboardNotifier>();
builder.Services.AddSingleton<IDashboardNotifier>(sp =>
    sp.GetRequiredService<DashboardNotifier>());

// ── Manual close needs a trade executor ───────────────────
// The dashboard needs ITradeExecutor only for the manual close button.
// We register it here but it will NOT log paper trading banners —
// that's a Bot concern. We suppress the constructor log by using
// a thin wrapper that skips the banner.
builder.Services.AddSingleton<ITradeExecutor, PaperTradeExecutorService>();

// ── Wallet monitor — no-op for dashboard ─────────────────
// Dashboard never monitors wallets directly — Bot does that.
// We register a no-op so controllers that inject IWalletMonitor compile.
builder.Services.AddSingleton<IWalletMonitor, NoOpWalletMonitor>();

// ── PositionManager — needed by controllers ───────────────
builder.Services.AddSingleton<IPositionManager, PositionManagerService>();

// ── Build + middleware pipeline ───────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
    app.UseExceptionHandler("/Dashboard/Error");

app.UseStaticFiles();   // serves wwwroot/css/site.css and wwwroot/js/site.js
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapHub<DashboardHub>("/hub/dashboard");  // SignalR endpoint

app.Logger.LogInformation("SolSnipe Dashboard running at {Url}",
    builder.Configuration["Urls"] ?? "http://localhost:5000");

app.Run();