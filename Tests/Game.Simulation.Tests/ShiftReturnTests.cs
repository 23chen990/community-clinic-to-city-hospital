using Game.Simulation;
using Xunit;
namespace Game.Simulation.Tests;

// CURRENT §5.4 plus user's 2026-10-08 decision: stable unfinished patients may return next day.
public sealed class ShiftReturnTests
{
    private static M1SimulationConfig ClosingScenario => new()
    {
        ShiftLengthTicks = 100,
        ClosingDurationTicks = 20,
        ClosingExtensionTicks = 10,
        ServiceTicks = 10,
        LabAnalysisServiceTicks = 200,
        NormalPublishIntervalTicks = 500,
        RiskIncreasePerWaitingTick = 0,
        PatienceLossPerWaitingTick = 0,
        CriticalTransferWaitTicks = 10_000,
        ReturnArrivalDelayTicks = 1,
    };

    [Fact]
    public void FinishingAllPatientsDoesNotEndTheWhiteShiftEarly()
    {
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "DEHYDRATION")],
            ClosingScenario with { ServiceTicks = 1, TreatmentServiceTicks = 1 });
        simulation.AdvanceTicks(50);
        Assert.Equal(1, simulation.Snapshot.Summary.DischargedPatients);
        Assert.False(simulation.IsShiftComplete);
        simulation.AdvanceTicks(50);
        Assert.True(simulation.IsShiftComplete);
        Assert.Equal(100, simulation.Tick);
        var hash = simulation.StateHash;
        simulation.AdvanceTicks(100);
        Assert.Equal(hash, simulation.StateHash);
        Assert.Single(simulation.Events, e => e.EventType == SimulationEventType.ShiftSettled);
    }

    [Fact]
    public void StableUnfinishedPatientsLeaveWithoutFalseDischargeOrRevenue()
    {
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "URTI", 1_000)], ClosingScenario);
        simulation.AdvanceTicks(120);
        Assert.True(simulation.IsShiftComplete);
        Assert.Equal(PatientStage.LeftUnfinished, Assert.Single(simulation.Snapshot.Patients).Stage);
        Assert.Equal(1, simulation.Snapshot.Summary.UnfinishedPatients);
        Assert.Equal(0, simulation.Snapshot.Summary.DischargedPatients);
        Assert.Equal(0, simulation.Snapshot.Summary.TransferredPatients);
        Assert.Empty(simulation.LedgerEntries);
        Assert.All(simulation.Snapshot.ResourceUsage.Values, count => Assert.Equal(0, count));
        Assert.Equal(0, simulation.Snapshot.OrphanTaskCount);
    }

    [Fact]
    public void ReturningPatientsResumeTheirExistingLabWorkWithoutRepeatingConsultOrSampling()
    {
        var config = ClosingScenario with { MinReturnChanceBasisPoints = 10_000, MaxReturnChanceBasisPoints = 10_000 };
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "URTI", 1_000)], config);
        simulation.AdvanceTicks(120);
        simulation.Submit(new SimulationCommand(1, simulation.Tick, SimulationCommandType.ArrangeNextDay));
        simulation.AdvanceTick();
        Assert.Equal(2, simulation.Day);
        Assert.Contains(simulation.Events, e => e.EventType == SimulationEventType.PatientReturnedNextDay);
        Assert.Single(simulation.Events, e => e.EventType == SimulationEventType.ConsultationStarted);
        Assert.Single(simulation.Events, e => e.EventType == SimulationEventType.LabSamplingStarted);
        Assert.Equal("患者001", Assert.Single(simulation.Snapshot.Patients).PatientId);
        Assert.Equal(1, simulation.Snapshot.Summary.ReturningPatients);
        Assert.DoesNotContain(simulation.Events, e => e.EventType == SimulationEventType.PatientTransferred);
    }

    [Fact]
    public void HighRiskPatientsUseSafetyTransferInsteadOfUncertainNextDayReturn()
    {
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "HIGH_RISK_ABDOMINAL", 7_000)],
            ClosingScenario with { MinReturnChanceBasisPoints = 10_000, MaxReturnChanceBasisPoints = 10_000 });
        simulation.AdvanceTicks(120);
        Assert.Equal(PatientStage.Transferred, Assert.Single(simulation.Snapshot.Patients).Stage);
        Assert.Equal(1, simulation.Snapshot.Summary.TransferredPatients);
        Assert.Equal(0, simulation.Snapshot.Summary.UnfinishedPatients);
        Assert.Empty(simulation.LedgerEntries);
        simulation.Submit(new SimulationCommand(1, simulation.Tick, SimulationCommandType.ArrangeNextDay));
        simulation.AdvanceTicks(2);
        Assert.DoesNotContain(simulation.Events, e => e.EventType == SimulationEventType.PatientReturnedNextDay);
    }

    [Fact]
    public void ClosingExtensionCanBeRequestedOnlyOnceWithHighRiskPatients()
    {
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "HIGH_RISK_ABDOMINAL", 7_000)], ClosingScenario);
        simulation.AdvanceTicks(100);
        simulation.Submit(new SimulationCommand(1, simulation.Tick, SimulationCommandType.ExtendClosing));
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(1).Status);
        simulation.Submit(new SimulationCommand(2, simulation.Tick, SimulationCommandType.ExtendClosing));
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.RejectedInvalid, simulation.GetCommandResult(2).Status);
        simulation.AdvanceTicks(27);
        Assert.False(simulation.IsShiftComplete);
        simulation.AdvanceTick();
        Assert.True(simulation.IsShiftComplete);
        Assert.Equal(130, simulation.Tick);
    }

    [Fact]
    public void ReturnChanceRewardsPatienceAndEvidenceAndPenalizesLongWaits()
    {
        var policy = new PatientReturnPolicy(new M1SimulationConfig());
        var baseline = policy.ChanceBasisPoints(4_000, 100, false);
        Assert.True(policy.ChanceBasisPoints(8_000, 100, false) > baseline);
        Assert.True(policy.ChanceBasisPoints(4_000, 100, true) > baseline);
        Assert.True(policy.ChanceBasisPoints(4_000, 500, false) < baseline);
    }

    [Fact]
    public void NewPatientCannotStartRegistrationOnItsArrivalTick()
    {
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "DEHYDRATION")]);
        simulation.AdvanceTick();
        Assert.Contains(simulation.Events, e => e.EventType == SimulationEventType.PatientEntered);
        Assert.DoesNotContain(simulation.Events, e => e.EventType == SimulationEventType.RegistrationStarted);
    }
}
