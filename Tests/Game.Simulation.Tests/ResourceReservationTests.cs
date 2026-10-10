using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class ResourceReservationTests
{
    [Fact]
    public void MultiResourceReservationIsAllOrNothingWhenOneResourceIsUnavailable()
    {
        var pool = new AtomicResourcePool(
            ("station", 1),
            ("technician", 1));

        Assert.True(pool.TryReserve(["station"], out var heldStation));

        Assert.False(pool.TryReserve(["station", "technician"], out _));
        Assert.Equal(1, pool.Usage()["station"]);
        Assert.Equal(0, pool.Usage()["technician"]);

        Assert.True(pool.Release(heldStation));
    }

    [Fact]
    public void RepeatedResourceIdsUseTheRequestedQuantityAndNeverOverAllocate()
    {
        var pool = new AtomicResourcePool(("technician", 2));

        Assert.True(pool.TryReserve(["technician", "technician"], out var reservationId));
        Assert.Equal(2, pool.Usage()["technician"]);

        Assert.False(pool.TryReserve(["technician"], out _));
        Assert.Equal(2, pool.Usage()["technician"]);

        Assert.True(pool.Release(reservationId));
        Assert.Equal(0, pool.Usage()["technician"]);
    }

    [Fact]
    public void FailedReservationWithAnUnknownResourceDoesNotPartiallyReserveKnownResources()
    {
        var pool = new AtomicResourcePool(("station", 1));

        Assert.False(pool.TryReserve(["station", "missing"], out _));
        Assert.Equal(0, pool.Usage()["station"]);
        Assert.Equal(0, pool.NegativeResourceCount);
    }

    [Fact]
    public void ReleasingTheSameReservationIdTwiceIsIdempotent()
    {
        var pool = new AtomicResourcePool(("station", 1));

        Assert.True(pool.TryReserve(["station"], out var reservationId));
        Assert.True(pool.Release(reservationId));
        Assert.False(pool.Release(reservationId));

        Assert.Equal(0, pool.Usage()["station"]);
        Assert.Equal(0, pool.NegativeResourceCount);
    }

    [Fact]
    public void SoftReservationExpiresAtItsTtlAndReleasesAllResources()
    {
        var pool = new AtomicResourcePool(
            ("station", 1),
            ("technician", 1));

        Assert.True(pool.TryReserveSoft(
            ["station", "technician"],
            currentTick: 0,
            out var reservationId,
            ttlTicks: 50,
            cooldownTicks: 10));
        Assert.Equal(1, pool.Usage()["station"]);
        Assert.Equal(1, pool.Usage()["technician"]);

        Assert.Equal(0, pool.Expire(49));
        Assert.Equal(1, pool.Expire(50));
        Assert.False(pool.Release(reservationId));
        Assert.Equal(0, pool.Usage()["station"]);
        Assert.Equal(0, pool.Usage()["technician"]);
    }

    [Fact]
    public void ExpiredSoftReservationCannotRetryUntilCooldownHasElapsed()
    {
        var pool = new AtomicResourcePool(("station", 1));

        Assert.True(pool.TryReserveSoft(
            ["station"],
            currentTick: 0,
            out _,
            ttlTicks: 50,
            cooldownTicks: 10));
        Assert.Equal(1, pool.Expire(50));

        Assert.False(pool.TryReserveSoft(["station"], 59, out _));
        Assert.True(pool.TryReserveSoft(["station"], 60, out var retryId));
        Assert.True(pool.Release(retryId));
    }

    [Fact]
    public void ExpireDoesNotReleaseHardReservations()
    {
        var pool = new AtomicResourcePool(("station", 1));

        Assert.True(pool.TryReserve(["station"], out var reservationId));
        Assert.Equal(0, pool.Expire(10_000));
        Assert.Equal(1, pool.Usage()["station"]);
        Assert.True(pool.Release(reservationId));
    }

    [Fact]
    public void LegacyListReservationAndReleaseRemainCompatible()
    {
        var pool = new AtomicResourcePool(("station", 1));

        Assert.True(pool.TryReserve(["station"]));
        Assert.Equal(1, pool.Usage()["station"]);

        pool.Release(["station"]);

        Assert.Equal(0, pool.Usage()["station"]);
        Assert.Equal(0, pool.NegativeResourceCount);
    }
}
