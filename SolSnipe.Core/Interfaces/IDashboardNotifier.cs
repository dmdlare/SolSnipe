namespace SolSnipe.Core.Interfaces;

public interface IDashboardNotifier
{
    Task SendLogMessage(string level, string message);
    Task SendBotStatusChange(bool running, bool paperTrading);
}