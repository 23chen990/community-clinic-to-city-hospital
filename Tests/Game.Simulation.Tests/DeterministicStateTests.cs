using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

/// <summary>
/// SIM002: the authoritative replay hash must distinguish future-affecting
/// state while ignoring presentation controls such as pause and speed.
/// </summary>
public sealed class DeterministicStateTests
{
    [Fact]
    public void DifferentSimulationConfigProducesDifferentAuthoritativeHash()
    {
        var baseline = HospitalSimulation.CreateBaseline(seed: 701);
        var changed = HospitalSimulation.CreateBaseline(
            seed: 701,
            config: new M1SimulationConfig { NormalPublishIntervalTicks = 901 });

        Assert.NotEqual(baseline.StateHash, changed.StateHash);
    }

    [Fact]
    public void DifferentFutureArrivalPlanProducesDifferentAuthoritativeHash()
    {
        var arrivesSooner = HospitalSimulation.CreateScenario(
            seed: 702,
            [new PatientArrival(1, 50, "URTI")]);
        var arrivesLater = HospitalSimulation.CreateScenario(
            seed: 702,
            [new PatientArrival(1, 51, "URTI")]);

        Assert.NotEqual(arrivesSooner.StateHash, arrivesLater.StateHash);
    }

    [Fact]
    public void PendingDispatchCommandProducesDifferentAuthoritativeHash()
    {
        var withoutCommand = HospitalSimulation.CreateBaseline(seed: 703);
        var withCommand = HospitalSimulation.CreateBaseline(seed: 703);

        Assert.Equal(
            CommandResultStatus.Deferred,
            withCommand.Submit(SimulationCommand.DispatchLab(
                commandId: 7031,
                tick: withCommand.Tick,
                plan: LabDispatchPlan.TemporarySupport)).Status);

        Assert.NotEqual(withoutCommand.StateHash, withCommand.StateHash);
    }

    [Fact]
    public void StaleOperationalCommandIsRecordedInAuthoritativeHash()
    {
        var withoutStaleCommand = HospitalSimulation.CreateBaseline(seed: 705);
        var withStaleCommand = HospitalSimulation.CreateBaseline(seed: 705);
        withoutStaleCommand.AdvanceTick();
        withStaleCommand.AdvanceTick();
        var stale = SimulationCommand.DispatchLab(
            commandId: 7051,
            tick: 0,
            plan: LabDispatchPlan.TemporarySupport);

        Assert.Equal(CommandResultStatus.RejectedStale, withStaleCommand.Submit(stale).Status);
        Assert.NotEqual(withoutStaleCommand.StateHash, withStaleCommand.StateHash);
        Assert.Equal(CommandResultStatus.RejectedStale, withoutStaleCommand.Submit(stale).Status);
        Assert.Equal(CommandResultStatus.RejectedDuplicate, withStaleCommand.Submit(stale).Status);
    }

    [Fact]
    public void PauseAndSpeedControlsDoNotChangeHashAtTheSameAuthoritativeTick()
    {
        var plain = HospitalSimulation.CreateBaseline(seed: 704);
        var controlled = HospitalSimulation.CreateBaseline(seed: 704);

        controlled.Submit(SimulationCommand.Pause(7041, controlled.Tick));
        controlled.Submit(SimulationCommand.SetSpeed(7042, controlled.Tick, SimulationSpeed.Double));
        controlled.AdvanceTick();

        Assert.Equal(plain.Tick, controlled.Tick);
        Assert.Equal(plain.StateHash, controlled.StateHash);
    }
}
