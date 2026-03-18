using SolSnipe.Core.Interfaces;

namespace SolSnipe.Dashboard;
public class NoOpWalletMonitor : IWalletMonitor
{
    public event Func<string, string, string, Task>? OnWalletBuy;

    public Task StartAsync(IEnumerable<string> walletAddresses, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task AddWalletAsync(string address) => Task.CompletedTask;

    public Task RemoveWalletAsync(string address) => Task.CompletedTask;
}