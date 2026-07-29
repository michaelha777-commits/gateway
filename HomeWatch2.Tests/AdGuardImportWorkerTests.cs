namespace HomeWatch2.Tests;

using Xunit;

public sealed class AdGuardImportWorkerTests
{
    [Fact]
    public void FreshEntryAdvancesHighWaterWhileRecoveryRemainsIncomplete()
    {
        var checkpoint = new ImportCheckpoint
        {
            Source = "adguard-querylog",
            HighWaterTimestamp = new DateTime(2026, 7, 29, 4, 5, 19, DateTimeKind.Utc),
            HighWaterFingerprint = "old",
            RecoveryComplete = false,
            BackfillBefore = new DateTime(2026, 7, 28, 4, 5, 19, DateTimeKind.Utc),
        };
        var freshTimestamp = new DateTime(2026, 7, 29, 4, 10, 0, DateTimeKind.Utc);

        AdGuardImportWorker.AdvanceHighWater(checkpoint, freshTimestamp, "fresh");

        Assert.Equal(freshTimestamp, checkpoint.HighWaterTimestamp);
        Assert.Equal("fresh", checkpoint.HighWaterFingerprint);
        Assert.False(checkpoint.RecoveryComplete);
        Assert.NotNull(checkpoint.BackfillBefore);
    }

    [Fact]
    public void HistoricalEntryCannotMoveHighWaterBackwardDuringRecovery()
    {
        var highWater = new DateTime(2026, 7, 29, 4, 5, 19, DateTimeKind.Utc);
        var checkpoint = new ImportCheckpoint
        {
            Source = "adguard-querylog",
            HighWaterTimestamp = highWater,
            HighWaterFingerprint = "current",
            RecoveryComplete = false,
        };

        AdGuardImportWorker.AdvanceHighWater(checkpoint, highWater.AddDays(-1), "historical");

        Assert.Equal(highWater, checkpoint.HighWaterTimestamp);
        Assert.Equal("current", checkpoint.HighWaterFingerprint);
        Assert.False(checkpoint.RecoveryComplete);
    }
}
