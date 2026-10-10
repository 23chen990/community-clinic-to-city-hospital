namespace Game.Simulation;

/// <summary>
/// Converts presentation time expressed as integer milliseconds into fixed
/// authority ticks. The clock owns only elapsed simulation time; callers own
/// the simulation and may use the returned count to invoke AdvanceTick.
/// </summary>
public sealed class SimulationFrameClock
{
    public const int TickMilliseconds = 100;

    // Store half-millisecond units so Half speed can be represented without
    // floating point and a speed change cannot discard a fractional remainder.
    private const int HalfMillisecondUnitsPerTick = TickMilliseconds * 2;
    private long accumulatedHalfMillisecondUnits;

    public long RemainderMilliseconds => accumulatedHalfMillisecondUnits / 2;

    public int AdvanceMilliseconds(int realMilliseconds, bool paused, SimulationSpeed speed)
    {
        if (realMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(realMilliseconds));
        }

        if (paused || realMilliseconds == 0)
        {
            return 0;
        }

        var speedUnits = speed switch
        {
            SimulationSpeed.Half => 1,
            SimulationSpeed.Normal => 2,
            SimulationSpeed.Double => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, "未知模拟速度"),
        };

        accumulatedHalfMillisecondUnits = checked(
            accumulatedHalfMillisecondUnits + (long)realMilliseconds * speedUnits);
        var ticks = accumulatedHalfMillisecondUnits / HalfMillisecondUnitsPerTick;
        accumulatedHalfMillisecondUnits %= HalfMillisecondUnitsPerTick;

        return checked((int)ticks);
    }
}
