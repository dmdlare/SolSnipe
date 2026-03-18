namespace SolSnipe.Dashboard;


public class BotStateService
{
    private bool _isRunning = false;
    private bool _paperTrading = true;
    private double _virtualBalance = 0;
    private readonly object _lock = new();

    public bool IsRunning { get { lock (_lock) return _isRunning; } }
    public bool PaperTrading { get { lock (_lock) return _paperTrading; } }
    public double VirtualBalance { get { lock (_lock) return _virtualBalance; } }

    public void SetRunning(bool running)
    {
        lock (_lock) _isRunning = running;
    }

    public void SetPaperMode(bool paperMode)
    {
        lock (_lock) _paperTrading = paperMode;
    }

    public void SetVirtualBalance(double balance)
    {
        lock (_lock) _virtualBalance = balance;
    }

    public void Toggle()
    {
        lock (_lock) _isRunning = !_isRunning;
    }

    public void TogglePaperMode()
    {
        lock (_lock) _paperTrading = !_paperTrading;
    }
}