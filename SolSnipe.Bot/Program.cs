using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Serilog;
using Solnet.Rpc;
using Solnet.Wallet;
using SolSnipe.Bot.Workers;
using SolSnipe.Core.Helpers;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Core.Services;
using SolSnipe.Data;
using SolSnipe.Data.Repositories;

namespace SolSnipe.Bot;

internal sealed class Program
{
    private static async Task Main(string[] args)
    {
        // Use a fully configured Serilog logger from the start
        // so we can see exactly where startup hangs
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("=== SolSnipe starting up ===");
            Log.Information("Reading config...");

            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();

            var paperTrading = config.GetValue<bool>("Bot:PaperTrading", defaultValue: true);
            Log.Information("Paper trading: {Paper}", paperTrading);

            Log.Information("Building host...");

            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((ctx, services) =>
                {
                    Log.Information("Registering config...");
                    services.Configure<SolanaConfig>(config.GetSection("Solana"));
                    services.Configure<HeliusConfig>(config.GetSection("Helius"));
                    services.Configure<TradingConfig>(config.GetSection("Bot"));
                    services.Configure<WalletScorerConfig>(config.GetSection("WalletScorer"));

                    // Wallet + RPC only needed for live trading
                    if (!paperTrading)
                    {
                        Log.Information("Registering live trading services...");
                        services.AddSingleton<IRpcClient>(_ =>
                            ClientFactory.GetClient(config["Solana:RpcUrl"]!));

                        services.AddSingleton<Wallet>(_ =>
                        {
                            var key = config["Solana:WalletPrivateKey"]!;
                            if (string.IsNullOrEmpty(key) || key.StartsWith("YOUR_"))
                                throw new InvalidOperationException(
                                    "Set Solana:WalletPrivateKey in appsettings.json");
                            return new Wallet(key);
                        });
                    }

                    Log.Information("Registering HTTP clients...");
                    services.AddHttpClient("jupiter", c =>
                    {
                        c.BaseAddress = new Uri("https://quote-api.jup.ag");
                        c.Timeout = TimeSpan.FromSeconds(15);
                    });
                    services.AddHttpClient("price", c => { c.Timeout = TimeSpan.FromSeconds(10); });
                    services.AddHttpClient("helius", c => { c.Timeout = TimeSpan.FromSeconds(30); });
                    services.AddHttpClient("dexscreener", c =>
                    {
                        c.BaseAddress = new Uri("https://api.dexscreener.com");
                        c.Timeout = TimeSpan.FromSeconds(15);
                    });

                    Log.Information("Registering database...");
                    services.AddSingleton<SolSnipeDb>();
                    services.AddSingleton<IPositionRepository, PositionRepository>();
                    services.AddSingleton<IWalletRepository, WalletRepository>();
                    services.AddSingleton<ITradeHistoryRepository, TradeHistoryRepository>();

                    Log.Information("Registering core services...");
                    services.AddSingleton<IPriceService, PriceService>();
                    services.AddSingleton<ISignalAggregator, SignalAggregatorService>();
                    services.AddSingleton<IWalletMonitor, WalletMonitorService>();
                    services.AddSingleton<IPositionManager, PositionManagerService>();
                    services.AddSingleton<IWalletDiscovery, WalletDiscoveryService>();
                    services.AddSingleton<IWalletScorer, WalletScorerService>();
                    services.AddSingleton<FilterHelper>();
                    services.AddSingleton<PaperTradingReportService>();

                    Log.Information("Registering trade executor (paper={Paper})...", paperTrading);
                    if (paperTrading)
                        services.AddSingleton<ITradeExecutor, PaperTradeExecutorService>();
                    else
                        services.AddSingleton<ITradeExecutor, TradeExecutorService>();

                    Log.Information("Registering workers...");
                    services.AddSingleton<WalletScorerWorker>();
                    services.AddHostedService<BotWorker>();
                    services.AddHostedService<PositionMonitorWorker>();

                    Log.Information("Registering Quartz...");
                    services.AddQuartz(q =>
                    {
                        var jobKey = new JobKey("WalletScorer");
                        var cronExp = config["WalletScorer:RescoringCronSchedule"] ?? "0 0 0 ? * SUN";

                        q.AddJob<WalletScorerWorker>(opts => opts
                            .WithIdentity(jobKey)
                            .StoreDurably());

                        q.AddTrigger(opts => opts
                            .ForJob(jobKey)
                            .WithIdentity("WalletScorer-trigger")
                            .WithCronSchedule(cronExp));
                    });
                    services.AddQuartzHostedService(q => q.WaitForJobsToComplete = false);

                    Log.Information("Service registration complete.");
                })
                .Build();

            Log.Information("Host built. Starting...");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed: {Message}", ex.Message);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}