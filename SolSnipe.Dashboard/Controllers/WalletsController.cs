using Microsoft.AspNetCore.Mvc;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Dashboard.Models;
using SolSnipe.Data.Repositories;

namespace SolSnipe.Dashboard.Controllers;

public class WalletsController : Controller
{
    private readonly IWalletRepository _wallets;
    private readonly IWalletMonitor _monitor;

    public WalletsController(IWalletRepository wallets, IWalletMonitor monitor)
    {
        _wallets = wallets;
        _monitor = monitor;
    }

    public IActionResult Index()
    {
        var wallets = _wallets.GetAll().Select(w => new WalletViewModel
        {
            Address = w.Address,
            Label = w.Label,
            Score = w.Score,
            WinRate = w.WinRate,
            AvgProfitPct = w.AvgProfitPct,
            AvgLossPct = w.AvgLossPct,
            TotalTrades = w.TotalTrades,
            LastScored = w.LastScored,
            IsActive = w.IsActive,
        }).ToList();

        return View(wallets);
    }

    [HttpPost]
    public async Task<IActionResult> Add(AddWalletRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length < 32)
        {
            TempData["Error"] = "Invalid Solana wallet address.";
            return RedirectToAction(nameof(Index));
        }

        // Check for duplicate
        if (_wallets.GetByAddress(request.Address) is not null)
        {
            TempData["Error"] = "Wallet already being tracked.";
            return RedirectToAction(nameof(Index));
        }

        var wallet = new TrackedWallet
        {
            Address = request.Address,
            Label = string.IsNullOrWhiteSpace(request.Label)
                ? $"MANUAL_{request.Address[..6]}" : request.Label,
            Score = 50,   // default score until rescored
            IsActive = true,
            AddedAt = DateTime.UtcNow,
            LastScored = DateTime.UtcNow,
        };

        _wallets.Upsert(wallet);
        await _monitor.AddWalletAsync(request.Address);

        TempData["Message"] = $"Wallet {wallet.Label} added and monitoring started.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(string address)
    {
        _wallets.Deactivate(address);
        await _monitor.RemoveWalletAsync(address);

        TempData["Message"] = $"Wallet {address[..8]}... removed from tracking.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Reactivate(string address)
    {
        var wallet = _wallets.GetByAddress(address);
        if (wallet is not null)
        {
            wallet.IsActive = true;
            _wallets.Upsert(wallet);
            await _monitor.AddWalletAsync(address);
            TempData["Message"] = $"Wallet {address[..8]}... reactivated.";
        }
        return RedirectToAction(nameof(Index));
    }
}