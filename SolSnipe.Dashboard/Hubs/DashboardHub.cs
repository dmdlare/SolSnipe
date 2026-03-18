using Microsoft.AspNetCore.SignalR;
using SolSnipe.Dashboard.Models;

namespace SolSnipe.Dashboard.Hubs;

/// <summary>
/// SignalR hub — pushes live updates to connected browsers.
/// The bot services call DashboardNotifier which broadcasts through here.
/// </summary>
public class DashboardHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", new
        {
            message = "Connected to SolSnipe dashboard",
            timestamp = DateTime.UtcNow.ToString("HH:mm:ss")
        });
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Injected into bot services to push real-time events to the dashboard.
/// </summary>
public class DashboardNotifier : SolSnipe.Core.Interfaces.IDashboardNotifier
{
    private readonly IHubContext<DashboardHub> _hub;

    public DashboardNotifier(IHubContext<DashboardHub> hub)
    {
        _hub = hub;
    }

    public Task SendSignalUpdate(SignalViewModel signal) =>
        _hub.Clients.All.SendAsync("SignalUpdate", signal);

    public Task SendPositionUpdate(PositionViewModel position) =>
        _hub.Clients.All.SendAsync("PositionUpdate", position);

    public Task SendPositionClosed(PositionViewModel position) =>
        _hub.Clients.All.SendAsync("PositionClosed", position);

    public Task SendLogMessage(string level, string message) =>
        _hub.Clients.All.SendAsync("LogMessage", new
        {
            level,
            message,
            time = DateTime.UtcNow.ToString("HH:mm:ss")
        });

    public Task SendBotStatusChange(bool running, bool paperTrading) =>
        _hub.Clients.All.SendAsync("BotStatusChange", new { running, paperTrading });

    public Task SendPriceUpdate(string tokenMint, double price, double pnlPct) =>
        _hub.Clients.All.SendAsync("PriceUpdate", new { tokenMint, price, pnlPct });

    public Task SendBalanceUpdate(double virtualBalance) =>
        _hub.Clients.All.SendAsync("BalanceUpdate", new { virtualBalance });
}