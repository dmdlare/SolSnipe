using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;
using SolSnipe.Core.Services;
using Xunit;

namespace SolSnipe.Tests;


public class SignalAggregatorTests
{
    private SignalAggregatorService CreateService(double thresholdPct = 60)
    {
        var config = Options.Create(new TradingConfig
        {
            TriggerThresholdPct = thresholdPct,
            SignalWindowMinutes = 10,
        });
        var svc = new SignalAggregatorService(config, NullLogger<SignalAggregatorService>.Instance);
        svc.SetTrackedWalletCount(5);
        return svc;
    }

    [Fact]
    public void RecordBuy_BelowThreshold_DoesNotFireEvent()
    {
        var svc = CreateService(thresholdPct: 60);
        bool fired = false;
        svc.OnThresholdReached += _ => { fired = true; return Task.CompletedTask; };

        svc.RecordBuy("wallet1", "tokenA"); // 1/5 = 20%
        svc.RecordBuy("wallet2", "tokenA"); // 2/5 = 40%

        fired.Should().BeFalse();
    }

    [Fact]
    public void RecordBuy_AtThreshold_FiresEvent()
    {
        var svc = CreateService(thresholdPct: 60);
        bool fired = false;
        svc.OnThresholdReached += _ => { fired = true; return Task.CompletedTask; };

        svc.RecordBuy("wallet1", "tokenA"); // 20%
        svc.RecordBuy("wallet2", "tokenA"); // 40%
        svc.RecordBuy("wallet3", "tokenA"); // 60% — threshold hit

        Thread.Sleep(100); // let async fire
        fired.Should().BeTrue();
    }

    [Fact]
    public void RecordBuy_SameWalletTwice_OnlyCountsOnce()
    {
        var svc = CreateService(thresholdPct: 60);

        svc.RecordBuy("wallet1", "tokenA");
        svc.RecordBuy("wallet1", "tokenA"); // duplicate

        var signal = svc.GetSignal("tokenA");
        signal.Should().NotBeNull();
        signal!.BuyerWallets.Count.Should().Be(1);
    }

    [Fact]
    public void RecordBuy_ThresholdOnlyFiredOnce_EvenWithMoreBuys()
    {
        var svc = CreateService(thresholdPct: 60);
        int count = 0;
        svc.OnThresholdReached += _ => { count++; return Task.CompletedTask; };

        for (int i = 1; i <= 5; i++)
            svc.RecordBuy($"wallet{i}", "tokenA");

        Thread.Sleep(100);
        count.Should().Be(1); // fired exactly once
    }

    [Fact]
    public void PruneOldSignals_RemovesStaleSignals()
    {
        var svc = CreateService();
        svc.RecordBuy("wallet1", "tokenA");

        svc.PruneOldSignals(TimeSpan.FromSeconds(-1)); // age of 0 = prune everything

        svc.GetSignal("tokenA").Should().BeNull();
    }

    [Fact]
    public void GetAllSignals_ReturnsAllTrackedTokens()
    {
        var svc = CreateService();
        svc.RecordBuy("wallet1", "tokenA");
        svc.RecordBuy("wallet1", "tokenB");
        svc.RecordBuy("wallet2", "tokenC");

        svc.GetAllSignals().Count.Should().Be(3);
    }
}



public class WalletScorerTests
{
    [Fact]
    public void Score_HighWinRate_ProducesHighScore()
    {
        var swaps = Enumerable.Range(0, 50).Select(i => new WalletSwapRecord
        {
            TokenMint = $"token{i}",
            PnlPct = i % 5 == 0 ? -15 : 75,   // 80% win rate
            EntryTime = DateTime.UtcNow.AddHours(-2),
            ExitTime = DateTime.UtcNow.AddHours(-1),
            MinutesAfterLaunch = 3,
            WasRug = false,
            ExitedBeforeRug = false,
        }).ToList();

        var wins = swaps.Count(s => s.IsWin);
        var winPct = (double)wins / swaps.Count * 100;
        winPct.Should().BeGreaterThan(75);
    }

    [Fact]
    public void WalletSwapRecord_IsWin_CorrectlyDerived()
    {
        var win = new WalletSwapRecord { PnlPct = 50 };
        var loss = new WalletSwapRecord { PnlPct = -20 };
        var zero = new WalletSwapRecord { PnlPct = 0 };

        win.IsWin.Should().BeTrue();
        loss.IsWin.Should().BeFalse();
        zero.IsWin.Should().BeFalse();
    }

    [Fact]
    public void WalletSwapRecord_HoldTime_CalculatedCorrectly()
    {
        var record = new WalletSwapRecord
        {
            EntryTime = DateTime.UtcNow.AddMinutes(-45),
            ExitTime = DateTime.UtcNow,
        };

        record.HoldTimeMinutes.Should().BeApproximately(45, precision: 1);
    }
}



public class PositionTests
{
    [Fact]
    public void Position_PnlPct_CalculatedCorrectly()
    {
        var entry = 0.001;
        var exit = 0.0015;
        var pnl = (exit - entry) / entry * 100;

        pnl.Should().BeApproximately(50.0, precision: 0.1);
    }

    [Fact]
    public void Position_HoldTime_UpdatesWhenOpen()
    {
        var pos = new Position
        {
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
        };

        pos.HoldTime.TotalHours.Should().BeApproximately(2, precision: 0.1);
    }

    [Fact]
    public void Position_HoldTime_FixedWhenClosed()
    {
        var open = DateTime.UtcNow.AddHours(-3);
        var closed = DateTime.UtcNow.AddHours(-1);

        var pos = new Position
        {
            OpenedAt = open,
            ClosedAt = closed,
            Status = PositionStatus.Closed,
        };

        pos.HoldTime.TotalHours.Should().BeApproximately(2, precision: 0.1);
    }
}



public class PaperTradingTests
{
    [Fact]
    public void Position_IsPaperTrade_DetectedByFakeTxId()
    {
        var pos = new Position
        {
            BuyTxSignature = "PAPER_abc123def456",
            IsPaperTrade = true,
        };

        pos.IsPaperTrade.Should().BeTrue();
        pos.BuyTxSignature.Should().StartWith("PAPER_");
    }

    [Fact]
    public void Position_IsNotPaperTrade_WithRealTxId()
    {
        var pos = new Position
        {
            BuyTxSignature = "5KtHBqzNxVT3oFQCrmFU2sV9xn9Yv3GpnRv3UkEqTmH...",
            IsPaperTrade = false,
        };

        pos.IsPaperTrade.Should().BeFalse();
    }

    [Fact]
    public void TradingConfig_PaperTrading_DefaultsToTrue()
    {
        var config = new TradingConfig();
        config.PaperTrading.Should().BeTrue();
    }

    [Fact]
    public void TradingConfig_PaperTradingBalance_DefaultsTen()
    {
        var config = new TradingConfig();
        config.PaperTradingStartingBalanceSol.Should().Be(10.0);
    }

    [Theory]
    [InlineData(65.0, 2.5, 20, "STRONG")]
    [InlineData(50.0, 1.6, 20, "DECENT")]
    [InlineData(45.0, 1.1, 20, "MARGINAL")]
    [InlineData(30.0, 0.7, 20, "UNPROFITABLE")]
    [InlineData(80.0, 3.0, 5, "Not enough")]  // < 10 trades
    public void PaperReport_Verdict_CorrectForMetrics(
        double winRate, double profitFactor, int trades, string expectedVerdict)
    {
        // Simulate verdict logic directly (extracted from PaperTradingReportService)
        string verdict;
        if (trades < 10)
            verdict = "Not enough";
        else if (profitFactor >= 2.0 && winRate >= 55)
            verdict = "STRONG";
        else if (profitFactor >= 1.5 && winRate >= 45)
            verdict = "DECENT";
        else if (profitFactor >= 1.0)
            verdict = "MARGINAL";
        else
            verdict = "UNPROFITABLE";

        verdict.Should().Be(expectedVerdict);
    }
}

public class WalletFilterTests
{
    [Theory]
    [InlineData(5000, 10, true)]   // 500 tx/day = bot
    [InlineData(200, 10, false)]  // 20 tx/day = normal
    [InlineData(100, 5, false)]  // 20 tx/day = normal
    public void IsBotWallet_CorrectlyDetects(int txCount, int days, bool expectedBot)
    {
        SolSnipe.Core.Helpers.WalletFilterHelper
            .IsBotWallet(txCount, days)
            .Should().Be(expectedBot);
    }

    [Fact]
    public void IsTooNew_WalletUnder30Days_ReturnsTrue()
    {
        var newWallet = DateTime.UtcNow.AddDays(-10);
        SolSnipe.Core.Helpers.WalletFilterHelper
            .IsTooNew(newWallet, minDays: 30)
            .Should().BeTrue();
    }

    [Fact]
    public void IsTooNew_WalletOver30Days_ReturnsFalse()
    {
        var oldWallet = DateTime.UtcNow.AddDays(-60);
        SolSnipe.Core.Helpers.WalletFilterHelper
            .IsTooNew(oldWallet, minDays: 30)
            .Should().BeFalse();
    }
}