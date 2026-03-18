using Microsoft.AspNetCore.Mvc;
using SolSnipe.Core.Interfaces;
using SolSnipe.Dashboard.Models;

namespace SolSnipe.Dashboard.Controllers;

public class SignalsController : Controller
{
    private readonly ISignalAggregator _signals;
    private readonly IPriceService _prices;

    public SignalsController(ISignalAggregator signals, IPriceService prices)
    {
        _signals = signals;
        _prices = prices;
    }

    public IActionResult Index()
    {
        var signals = _signals.GetAllSignals()
            .OrderByDescending(s => s.SignalStrengthPct)
            .Select(SignalViewModel.FromSignal)
            .ToList();

        return View(signals);
    }

    // JSON endpoint for live signal polling
    [HttpGet]
    public IActionResult Live()
    {
        var signals = _signals.GetAllSignals()
            .OrderByDescending(s => s.SignalStrengthPct)
            .Select(SignalViewModel.FromSignal)
            .ToList();

        return Json(signals);
    }
}