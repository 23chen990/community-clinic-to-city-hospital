using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class SimulationCommandTests
{
    [Fact]
    public void SubmittedCommandIsDeferredThenQueryReturnsAuthoritativeAcceptedResult()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 101);
        var command = SimulationCommand.SetSpeed(1001, simulation.Tick, SimulationSpeed.Double);

        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);
        Assert.Equal(CommandResultStatus.Deferred, simulation.GetCommandResult(command.CommandId).Status);

        simulation.AdvanceTick();

        var result = simulation.GetCommandResult(command.CommandId);
        Assert.Equal(CommandResultStatus.Accepted, result.Status);
        Assert.Equal(SimulationSpeed.Double, simulation.Speed);
    }

    [Fact]
    public void DuplicateSubmissionReturnsExistingResultAndExecutesOnce()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 102);
        var command = SimulationCommand.SetSpeed(1002, simulation.Tick, SimulationSpeed.Double);

        var first = simulation.Submit(command);
        var second = simulation.Submit(command);
        Assert.Equal(CommandResultStatus.Deferred, first.Status);
        Assert.Equal(CommandResultStatus.RejectedDuplicate, second.Status);
        Assert.Single(simulation.CommandLog, entry => entry.CommandId == command.CommandId);

        simulation.AdvanceTick();

        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(command.CommandId).Status);
        Assert.Equal(SimulationSpeed.Double, simulation.Speed);
        Assert.Single(simulation.Events, entry => entry.EventType == SimulationEventType.SpeedChanged);
    }

    [Fact]
    public void CommandSubmittedForAnEarlierTickIsRejectedAsStaleAndQueryable()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 103);
        simulation.AdvanceTick();

        var command = SimulationCommand.Pause(1003, simulation.Tick - 1);
        var result = simulation.Submit(command);

        Assert.Equal(CommandResultStatus.RejectedStale, result.Status);
        Assert.Equal(CommandResultStatus.RejectedStale,
            simulation.GetCommandResult(command.CommandId).Status);
        Assert.False(simulation.Paused);
    }

    [Fact]
    public void InvalidAndInsufficientCommandsAreRejectedAtTheAuthoritativeTick()
    {
        var config = new M1SimulationConfig
        {
            InitialCashMinorUnits = 0,
            OutsourceCostPerSampleMinorUnits = 1,
            MaxOutsourceSamples = 2,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
            NormalPublishIntervalTicks = 5_000,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 104,
            arrivals:
            [
                new PatientArrival(1, 1, "URTI", RiskBasisPoints: 1_000),
                new PatientArrival(2, 1, "URTI", RiskBasisPoints: 1_000),
                new PatientArrival(3, 1, "URTI", RiskBasisPoints: 1_000),
            ],
            config);
        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.AnalysisQueue >= 2, 2_000));
        var invalid = new SimulationCommand(
            1004,
            simulation.Tick,
            SimulationCommandType.SetSpeed,
            Speed: (SimulationSpeed)99);
        var insufficient = SimulationCommand.DispatchLab(
            1005,
            simulation.Tick,
            LabDispatchPlan.OutsourceSamples);

        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(invalid).Status);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(insufficient).Status);
        simulation.AdvanceTick();

        Assert.Equal(CommandResultStatus.RejectedInvalid,
            simulation.GetCommandResult(invalid.CommandId).Status);
        Assert.Equal(CommandResultStatus.RejectedInsufficient,
            simulation.GetCommandResult(insufficient.CommandId).Status);
    }

    [Fact]
    public void CommandsCanBeAcceptedWhilePausedWithoutAdvancingTheAuthoritativeTick()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 105);
        var pause = SimulationCommand.Pause(1006, simulation.Tick);
        var speed = SimulationCommand.SetSpeed(1007, simulation.Tick, SimulationSpeed.Double);

        simulation.Submit(pause);
        simulation.Submit(speed);
        simulation.AdvanceTick();
        var pausedTick = simulation.Tick;

        simulation.AdvanceTick();

        Assert.Equal(pausedTick, simulation.Tick);
        Assert.True(simulation.Paused);
        Assert.Equal(SimulationSpeed.Double, simulation.Speed);
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(pause.CommandId).Status);
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(speed.CommandId).Status);
    }

    [Fact]
    public void ANewCriticalPatientLowersSpeedOnceAndManualRecoveryPersists()
    {
        var config = new M1SimulationConfig
        {
            ServiceTicks = 100,
            TreatmentServiceTicks = 100,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
            CriticalTransferWaitTicks = 10_000,
            NormalPublishIntervalTicks = 5_000,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 106,
            arrivals:
            [
                new PatientArrival(1, 1, "URTI", RiskBasisPoints: 9_000),
            ],
            config);
        var speedCommand = SimulationCommand.SetSpeed(1008, simulation.Tick, SimulationSpeed.Double);
        simulation.Submit(speedCommand);
        simulation.AdvanceTick();

        var criticalPatient = Assert.Single(simulation.Snapshot.Patients,
            patient => patient.RiskLevel == RiskLevel.Critical);
        Assert.Equal(SimulationSpeed.Half, simulation.Speed);
        Assert.Contains(simulation.Events, entry =>
            entry.EventType == SimulationEventType.SpeedChanged &&
            entry.Detail == criticalPatient.PatientId);

        var restore = SimulationCommand.SetSpeed(1009, simulation.Tick, SimulationSpeed.Double);
        simulation.Submit(restore);
        simulation.AdvanceTick();
        Assert.Equal(SimulationSpeed.Double, simulation.Speed);

        simulation.AdvanceTicks(10);
        Assert.Equal(SimulationSpeed.Double, simulation.Speed);
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(restore.CommandId).Status);
    }
}
