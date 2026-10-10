using Game.Simulation;
using Xunit;
namespace Game.Simulation.Tests;

// CURRENT §5.4, DAY-001, DAY-005.
public sealed class ShiftBoundaryTests
{
    [Fact]
    public void EachArrivalClassStopsAtItsOwnBoundary()
    {
        var simulation = HospitalSimulation.CreateScenario(7,
        [
            new(1, 2999, "DEHYDRATION", 1000),
            new(2, 3000, "DEHYDRATION", 1000),
            new(3, 3299, "DEHYDRATION", 1000, Kind: PatientArrivalKind.Story),
            new(4, 3300, "DEHYDRATION", 1000, Kind: PatientArrivalKind.Story),
            new(5, 3599, "DEHYDRATION", 1000, Kind: PatientArrivalKind.Urgent),
            new(6, 3600, "DEHYDRATION", 1000, Kind: PatientArrivalKind.Urgent),
        ], new M1SimulationConfig {RiskIncreasePerWaitingTick = 0});
        simulation.AdvanceTicks(3600);
        Assert.Equal(new[] { "患者001", "患者003", "患者005" }, simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.PatientEntered).Select(entry => entry.PatientId));
        Assert.Equal(ShiftPhase.Closing, simulation.Phase);
    }

    [Fact]
    public void NextDayCommandsCannotSkipDaysOrDuplicateSettlements()
    {
        var simulation = HospitalSimulation.CreateScenario(7, Array.Empty<PatientArrival>());
        simulation.Submit(new SimulationCommand(1, 0, SimulationCommandType.ArrangeNextDay));
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.RejectedInvalid, simulation.GetCommandResult(1).Status);
        simulation.AdvanceTicks(3599);
        Assert.Single(simulation.CompletedShifts);
        var command = new SimulationCommand(2, simulation.Tick, SimulationCommandType.ArrangeNextDay);
        simulation.Submit(command);
        simulation.Submit(command);
        simulation.Submit(new SimulationCommand(3, simulation.Tick, SimulationCommandType.ArrangeNextDay));
        simulation.AdvanceTick();
        Assert.Equal(2, simulation.Day);
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(2).Status);
        Assert.Equal(CommandResultStatus.RejectedInvalid, simulation.GetCommandResult(3).Status);
        Assert.Single(simulation.CompletedShifts);
    }

    [Fact]
    public void AdvanceUntilNeverSpendsAnExtraTickBeyondItsBudget()
    {
        var simulation = HospitalSimulation.CreateBaseline(7);
        Assert.False(simulation.AdvanceUntil(_ => false, 0));
        Assert.Equal(0, simulation.Tick);
        Assert.False(simulation.AdvanceUntil(_ => false, 5));
        Assert.Equal(5, simulation.Tick);
    }

    [Fact]
    public void ResultPublicationKeepsArrivalOrderWhenEntityIdsHaveADifferentOrder()
    {
        var simulation = HospitalSimulation.CreateScenario(7,
            [new(10, 1, "URTI", 1000), new(1, 2, "URTI", 1000)],
            new M1SimulationConfig {NormalPublishIntervalTicks = 100, LabAnalysisServiceTicks = 10, RiskIncreasePerWaitingTick = 0});
        Assert.True(simulation.AdvanceUntil(_ => simulation.Events.Any(e => e.EventType == SimulationEventType.ResultPublished), 200));
        Assert.Equal("患者010", simulation.Events.First(e => e.EventType == SimulationEventType.ResultPublished).PatientId);
    }

    [Fact]
    public void BaselineRespectsConfiguredInitialPatience()
    {
        var simulation = HospitalSimulation.CreateBaseline(7, new M1SimulationConfig {PatientPatienceBasisPoints = 4321});
        Assert.All(simulation.Snapshot.Patients, patient => Assert.Equal(4321, patient.PatienceBasisPoints));
    }
}
