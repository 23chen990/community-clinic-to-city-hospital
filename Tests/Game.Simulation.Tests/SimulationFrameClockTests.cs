using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class SimulationFrameClockTests
{
    [Fact]
    public void NormalSpeedConvertsIntegerMillisecondsToTenHertzTicks()
    {
        var clock = new SimulationFrameClock();

        Assert.Equal(10, clock.AdvanceMilliseconds(1_000, paused: false, SimulationSpeed.Normal));
        Assert.Equal(0, clock.RemainderMilliseconds);
    }

    [Fact]
    public void HalfAndDoubleSpeedAccumulateIntegerRemaindersWithoutFloatingPoint()
    {
        var half = new SimulationFrameClock();
        Assert.Equal(0, half.AdvanceMilliseconds(199, paused: false, SimulationSpeed.Half));
        Assert.Equal(1, half.AdvanceMilliseconds(1, paused: false, SimulationSpeed.Half));
        Assert.Equal(0, half.RemainderMilliseconds);

        var doubled = new SimulationFrameClock();
        Assert.Equal(0, doubled.AdvanceMilliseconds(49, paused: false, SimulationSpeed.Double));
        Assert.Equal(1, doubled.AdvanceMilliseconds(1, paused: false, SimulationSpeed.Double));
        Assert.Equal(0, doubled.RemainderMilliseconds);
    }

    [Fact]
    public void PausedFramesDoNotAdvanceOrConsumeSimulationTime()
    {
        var clock = new SimulationFrameClock();

        Assert.Equal(0, clock.AdvanceMilliseconds(1_000, paused: true, SimulationSpeed.Double));
        Assert.Equal(0, clock.RemainderMilliseconds);
        Assert.Equal(1, clock.AdvanceMilliseconds(100, paused: false, SimulationSpeed.Normal));
    }

    [Fact]
    public void ChangingSpeedPreservesOnlyIntegerSimulationRemainder()
    {
        var clock = new SimulationFrameClock();

        Assert.Equal(0, clock.AdvanceMilliseconds(50, paused: false, SimulationSpeed.Normal));
        Assert.Equal(1, clock.AdvanceMilliseconds(25, paused: false, SimulationSpeed.Double));
        Assert.Equal(0, clock.RemainderMilliseconds);
    }
}
