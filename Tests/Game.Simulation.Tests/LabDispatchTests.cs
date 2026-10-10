using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class LabDispatchTests
{
    [Fact]
    public void RiskPriorityReordersResultsWithoutChangingPublicationCadence()
    {
        var config = new M1SimulationConfig
        {
            NormalPublishIntervalTicks = 1_000,
            RiskPriorityPublishIntervalTicks = 1,
        };
        var normal = HospitalSimulation.CreateBaseline(seed: 19, config);
        var prioritized = HospitalSimulation.CreateBaseline(seed: 19, config);

        Assert.True(normal.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        Assert.True(prioritized.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        Assert.Equal(normal.Tick, prioritized.Tick);

        var highestRiskPatient = prioritized.Snapshot.Patients
            .Where(patient => patient.Stage == PatientStage.ResultPending)
            .OrderByDescending(patient => patient.RiskBasisPoints)
            .ThenBy(patient => patient.PatientId, StringComparer.Ordinal)
            .First();

        var dispatch = SimulationCommand.DispatchLab(1, prioritized.Tick, LabDispatchPlan.RiskPriority);
        Assert.Equal(CommandResultStatus.Deferred, prioritized.Submit(dispatch).Status);

        Assert.True(normal.AdvanceUntil(
            _ => normal.Events.Any(entry => entry.EventType == SimulationEventType.ResultPublished),
            2_000));
        Assert.True(prioritized.AdvanceUntil(
            _ => prioritized.Events.Any(entry => entry.EventType == SimulationEventType.ResultPublished),
            2_000));

        var normalFirst = normal.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .OrderBy(entry => entry.Tick)
            .First();
        var prioritizedPublishes = prioritized.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .OrderBy(entry => entry.Tick)
            .ToArray();

        Assert.Equal(normalFirst.Tick, prioritizedPublishes[0].Tick);
        Assert.Equal(highestRiskPatient.PatientId, prioritizedPublishes[0].PatientId);

        Assert.True(prioritized.AdvanceUntil(
            _ => prioritized.Events.Count(entry => entry.EventType == SimulationEventType.ResultPublished) >= 2,
            2_000));
        prioritizedPublishes = prioritized.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .OrderBy(entry => entry.Tick)
            .ToArray();
        Assert.Equal(config.NormalPublishIntervalTicks, prioritizedPublishes[1].Tick - prioritizedPublishes[0].Tick);
    }

    [Fact]
    public void OutsourcingSelectsAtMostTwoUnanalyzedRoutineSamplesAndChargesPerSample()
    {
        var config = new M1SimulationConfig
        {
            InitialCashMinorUnits = 80,
            OutsourceCostPerSampleMinorUnits = 40,
            MaxOutsourceSamples = 2,
            OutsourceReturnTicks = 1_200,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
            NormalPublishIntervalTicks = 5_000,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 10,
            arrivals:
            [
                new PatientArrival(1, 1, "URTI", 1_000),
                new PatientArrival(2, 1, "URTI", 1_000),
                new PatientArrival(3, 1, "URTI", 1_000),
            ],
            config);

        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.AnalysisQueue >= 2, 2_000));
        var before = simulation.Snapshot;
        var command = SimulationCommand.DispatchLab(2, simulation.Tick, LabDispatchPlan.OutsourceSamples);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);

        simulation.AdvanceTick();

        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(command.CommandId).Status);
        Assert.Equal(before.Lab.ResultBacklog, simulation.Snapshot.Lab.ResultBacklog);
        Assert.Equal(before.Lab.AnalysisQueue - 2, simulation.Snapshot.Lab.AnalysisQueue);
        Assert.Equal(2, simulation.Snapshot.Lab.OutsourcedSamples);
        Assert.Equal(80, simulation.LedgerEntries
            .Where(entry => entry.Kind == LedgerKind.DispatchCost)
            .Sum(entry => entry.AmountMinorUnits));
        Assert.All(simulation.Snapshot.Patients.Where(patient => patient.Stage == PatientStage.Outsourced),
            patient => Assert.True(patient.RiskBasisPoints < 3_500));

        var started = simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.OutsourcedSampleStarted)
            .OrderBy(entry => entry.Tick)
            .ToArray();
        Assert.Equal(2, started.Length);

        Assert.True(simulation.AdvanceUntil(
            _ => simulation.Events.Count(entry => entry.EventType == SimulationEventType.OutsourcedResultReturned) == 2,
            config.OutsourceReturnTicks + 1));
        var returned = simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.OutsourcedResultReturned)
            .OrderBy(entry => entry.Tick)
            .ToArray();
        Assert.Equal(2, returned.Length);
        Assert.Equal(config.OutsourceReturnTicks, returned[0].Tick - started[0].Tick);
    }

    [Fact]
    public void OutsourcingChecksTheFullCostBeforeMovingAnySample()
    {
        var config = new M1SimulationConfig
        {
            InitialCashMinorUnits = 79,
            OutsourceCostPerSampleMinorUnits = 40,
            MaxOutsourceSamples = 2,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
            NormalPublishIntervalTicks = 5_000,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 10,
            arrivals:
            [
                new PatientArrival(1, 1, "URTI", 1_000),
                new PatientArrival(2, 1, "URTI", 1_000),
                new PatientArrival(3, 1, "URTI", 1_000),
            ],
            config);

        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.AnalysisQueue >= 2, 2_000));
        var before = simulation.Snapshot;
        var command = SimulationCommand.DispatchLab(3, simulation.Tick, LabDispatchPlan.OutsourceSamples);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);

        simulation.AdvanceTick();

        Assert.Equal(CommandResultStatus.RejectedInsufficient, simulation.GetCommandResult(command.CommandId).Status);
        Assert.Equal(before.Lab.AnalysisQueue, simulation.Snapshot.Lab.AnalysisQueue);
        Assert.Equal(0, simulation.Snapshot.Lab.OutsourcedSamples);
        Assert.DoesNotContain(simulation.LedgerEntries, entry => entry.Kind == LedgerKind.DispatchCost);
        Assert.DoesNotContain(simulation.Events, entry => entry.EventType == SimulationEventType.OutsourcedSampleStarted);
    }

    [Fact]
    public void TechnicianFatigueExtendsTheNormalPublishIntervalAfterSupportEnds()
    {
        var baseConfig = new M1SimulationConfig
        {
            TemporarySupportDurationTicks = 16,
            TemporarySupportPublishIntervalTicks = 5,
            NormalPublishIntervalTicks = 100,
            LabAnalysisServiceTicks = 1,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
        };
        var arrivals = new[]
        {
            new PatientArrival(1, 1, "URTI", 1_000),
            new PatientArrival(2, 1, "URTI", 1_000),
            new PatientArrival(3, 1, "URTI", 1_000),
            new PatientArrival(4, 1, "URTI", 1_000),
        };
        var rested = HospitalSimulation.CreateScenario(11, arrivals, baseConfig with
        {
            FatigueDurationPenaltyBasisPoints = 0,
        });
        var tired = HospitalSimulation.CreateScenario(11, arrivals, baseConfig with
        {
            FatigueDurationPenaltyBasisPoints = 10_000,
        });

        Assert.True(rested.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        Assert.True(tired.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        rested.Submit(SimulationCommand.DispatchLab(4, rested.Tick, LabDispatchPlan.TemporarySupport));
        tired.Submit(SimulationCommand.DispatchLab(4, tired.Tick, LabDispatchPlan.TemporarySupport));

        Assert.True(rested.AdvanceUntil(
            _ => rested.Events.Count(entry => entry.EventType == SimulationEventType.ResultPublished) == 4,
            2_000));
        Assert.True(tired.AdvanceUntil(
            _ => tired.Events.Count(entry => entry.EventType == SimulationEventType.ResultPublished) == 4,
            2_000));

        var restedLast = rested.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .Max(entry => entry.Tick);
        var tiredLast = tired.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .Max(entry => entry.Tick);
        Assert.Equal(15, tiredLast - restedLast);
    }
}
