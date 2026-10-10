using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class M1SimulationTests
{
    private static int FullShiftBudget(M1SimulationConfig config) =>
        config.ShiftLengthTicks + config.ClosingDurationTicks + config.ClosingExtensionTicks + 1;

    private static void AssertAllPatientsTerminal(HospitalSimulation simulation)
    {
        Assert.All(simulation.Snapshot.Patients, patient =>
            Assert.True(
                patient.Stage is PatientStage.Completed or PatientStage.Transferred or
                    PatientStage.LeftWithoutCare or PatientStage.LeftUnfinished,
                $"患者 {patient.PatientId} 在班次结束时仍处于 {patient.Stage}"));
    }

    [Fact]
    public void BaselinePatientsUseTheCompleteCarePathAndDischarge()
    {
        var config = new M1SimulationConfig();
        var simulation = HospitalSimulation.CreateBaseline(seed: 7, config);

        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        Assert.Equal(5, simulation.Snapshot.ArrivedPatients);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.PatientEntered);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.TriageCompleted);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.ConsultationStarted);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.LabSamplingStarted);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.LabAnalysisStarted);

        simulation.Submit(SimulationCommand.DispatchLab(1, simulation.Tick, LabDispatchPlan.TemporarySupport));
        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.ShiftComplete,
            FullShiftBudget(config)));

        Assert.Equal(5, simulation.Snapshot.Summary.DischargedPatients);
        Assert.Equal(0, simulation.Snapshot.Summary.TransferredPatients);
        Assert.True(simulation.Snapshot.Tick >= config.ShiftLengthTicks);
        Assert.Equal(ShiftPhase.Settled, simulation.Snapshot.Phase);
        AssertAllPatientsTerminal(simulation);
        Assert.Single(simulation.CompletedShifts);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.ResultReturnedToConsultation);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.TreatmentCompleted);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.PharmacyCompleted);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.PatientDischarged);
    }

    [Fact]
    public void PatientsUseNamedCaseTemplatesAndLowConfidenceRequestsLab()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 13);

        Assert.All(simulation.Snapshot.Patients, patient => Assert.False(string.IsNullOrWhiteSpace(patient.CaseId)));
        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        Assert.Equal(4, simulation.Events.Count(entry => entry.EventType == SimulationEventType.LabOrdered));
    }

    [Fact]
    public void TemporarySupportRunsForSixtySecondsAndReducesFourResultsToOne()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 11);
        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));

        var fatigueBefore = simulation.Snapshot.Lab.TechnicianFatigueBasisPoints;
        var command = SimulationCommand.DispatchLab(2, simulation.Tick, LabDispatchPlan.TemporarySupport);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);

        simulation.AdvanceTicks(HospitalSimulation.TemporarySupportDurationTicks);

        Assert.Equal(1, simulation.Snapshot.Lab.ResultBacklog);
        Assert.Equal(fatigueBefore + HospitalSimulation.TemporarySupportFatigueBasisPoints,
            simulation.Snapshot.Lab.TechnicianFatigueBasisPoints);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.TemporarySupportStarted);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.TemporarySupportEnded);
    }

    [Fact]
    public void LabAnalysisReservesTechnicianAndAnalyzerTogether()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 17);
        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Patients.Any(patient =>
            patient.Stage == PatientStage.InLabAnalysis), 2_000));

        Assert.Equal(1, simulation.Snapshot.ResourceUsage["LabTechnician"]);
        Assert.Equal(1, simulation.Snapshot.ResourceUsage["Analyzer"]);
    }

    [Fact]
    public void CriticalPatientTransfersWhenTheResultWaitExceedsTheSafetyBudget()
    {
        var config = new M1SimulationConfig
        {
            CriticalTransferWaitTicks = 20,
            NormalPublishIntervalTicks = 5_000,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 7,
            arrivals: [new PatientArrival(1, 1, "HIGH_RISK_ABDOMINAL", 8_900, 10_000)],
            config);

        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.Summary.TransferredPatients > 0,
            config.ShiftLengthTicks));
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.PatientTransferred);
        Assert.DoesNotContain(simulation.Snapshot.Patients, patient =>
            patient.RiskLevel == RiskLevel.Critical &&
            patient.Stage is PatientStage.LeftWithoutCare or PatientStage.LeftUnfinished);
    }

    [Fact]
    public void RiskPriorityPublishesTheHighestRiskResultFirst()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 19);
        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));

        var highestRisk = simulation.Snapshot.Patients
            .Where(patient => patient.Stage == PatientStage.ResultPending)
            .OrderByDescending(patient => patient.RiskBasisPoints)
            .First();

        simulation.Submit(SimulationCommand.DispatchLab(3, simulation.Tick, LabDispatchPlan.RiskPriority));
        Assert.True(simulation.AdvanceUntil(_ => simulation.Events.Any(entry =>
            entry.EventType == SimulationEventType.ResultPublished && entry.PatientId == highestRisk.PatientId), 2_000));

        var firstPublished = simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .OrderBy(entry => entry.Tick)
            .First();
        Assert.Equal(highestRisk.PatientId, firstPublished.PatientId);
    }

    [Fact]
    public void OutsourcedSamplesReturnToFollowUpAndCompleteTheJourney()
    {
        var config = new M1SimulationConfig
        {
            InitialCashMinorUnits = 80,
            OutsourceCostPerSampleMinorUnits = 40,
            MaxOutsourceSamples = 2,
            OutsourceReturnTicks = 1_200,
            LabAnalysisServiceTicks = 100,
            NormalPublishIntervalTicks = 5_000,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 29,
            arrivals:
            [
                new PatientArrival(1, 1, "URTI", 1_000),
                new PatientArrival(2, 1, "URTI", 1_000),
                new PatientArrival(3, 1, "URTI", 1_000),
            ],
            config);

        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.AnalysisQueue >= 2, 2_000));
        var before = simulation.Snapshot;

        var command = SimulationCommand.DispatchLab(9, simulation.Tick, LabDispatchPlan.OutsourceSamples);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);
        simulation.AdvanceTick();

        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(command.CommandId).Status);
        Assert.Equal(before.Lab.ResultBacklog, simulation.Snapshot.Lab.ResultBacklog);
        Assert.Equal(before.Lab.AnalysisQueue - 2, simulation.Snapshot.Lab.AnalysisQueue);
        Assert.Equal(2, simulation.Snapshot.Lab.OutsourcedSamples);
        Assert.Equal(80, simulation.LedgerEntries
            .Where(entry => entry.Kind == LedgerKind.DispatchCost)
            .Sum(entry => entry.AmountMinorUnits));
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.OutsourcedSampleStarted);

        Assert.True(simulation.AdvanceUntil(
            _ => simulation.Events.Count(entry => entry.EventType == SimulationEventType.OutsourcedResultReturned) == 2,
            config.OutsourceReturnTicks + 1));
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.OutsourcedResultReturned);
        Assert.Contains(simulation.Events, entry => entry.EventType == SimulationEventType.ResultReturnedToConsultation);
        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.ShiftComplete,
            FullShiftBudget(config)));
        Assert.True(simulation.Snapshot.Tick >= config.ShiftLengthTicks);
        Assert.Equal(ShiftPhase.Settled, simulation.Snapshot.Phase);
        AssertAllPatientsTerminal(simulation);
    }

    [Fact]
    public void RootCauseAndLedgerRemainTraceableAndIdempotent()
    {
        var config = new M1SimulationConfig();
        var simulation = HospitalSimulation.CreateBaseline(seed: 23, config);
        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000));
        simulation.Submit(SimulationCommand.DispatchLab(4, simulation.Tick, LabDispatchPlan.TemporarySupport));
        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.ShiftComplete,
            FullShiftBudget(config)));

        Assert.Contains(simulation.RootCauseEdges, edge =>
            edge.ReasonCode == RootCauseCode.ResultPublicationBacklog);
        Assert.Contains(simulation.RootCauseEdges, edge =>
            edge.ReasonCode == RootCauseCode.ResultReturnedToFollowUp);

        Assert.Equal(simulation.LedgerEntries.Count,
            simulation.LedgerEntries.Select(entry => entry.IdempotencyKey).Distinct().Count());
        Assert.Equal(simulation.Snapshot.Summary.DischargedPatients,
            simulation.LedgerEntries.Count(entry => entry.Kind == LedgerKind.PatientRevenue));
    }

    [Fact]
    public void PauseAndSpeedDoNotChangeTheAuthoritativeReplayHash()
    {
        var normal = HospitalSimulation.CreateBaseline(seed: 31);
        var fast = HospitalSimulation.CreateBaseline(seed: 31);

        normal.Submit(SimulationCommand.DispatchLab(5, normal.Tick, LabDispatchPlan.TemporarySupport));
        fast.Submit(SimulationCommand.DispatchLab(5, fast.Tick, LabDispatchPlan.TemporarySupport));
        fast.Submit(SimulationCommand.SetSpeed(6, fast.Tick, SimulationSpeed.Double));
        fast.Submit(SimulationCommand.Pause(7, fast.Tick));
        fast.AdvanceTicks(20);
        Assert.Equal(normal.Tick, fast.Tick);

        fast.Submit(SimulationCommand.Resume(8, fast.Tick));
        normal.AdvanceTicks(800);
        fast.AdvanceTicks(800);

        Assert.Equal(normal.StateHash, fast.StateHash);
    }

    [Fact]
    public void OneHundredDeterministicShiftsCompleteWithoutLeaksOrDuplicateSettlement()
    {
        for (var seed = 1; seed <= 100; seed++)
        {
            var config = new M1SimulationConfig();
            var simulation = HospitalSimulation.CreateBaseline(seed, config);
            Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.ResultBacklog == 4, 2_000), $"seed {seed} did not form backlog");
            simulation.Submit(SimulationCommand.DispatchLab(seed, simulation.Tick, LabDispatchPlan.TemporarySupport));

            Assert.True(
                simulation.AdvanceUntil(snapshot => snapshot.ShiftComplete, FullShiftBudget(config)),
                $"seed {seed} did not settle the shift");
            Assert.True(simulation.Snapshot.Tick >= config.ShiftLengthTicks, $"seed {seed} settled before white shift end");
            Assert.Equal(ShiftPhase.Settled, simulation.Snapshot.Phase);
            Assert.Single(simulation.CompletedShifts);
            AssertAllPatientsTerminal(simulation);
            Assert.Equal(0, simulation.Snapshot.OrphanTaskCount);
            Assert.Equal(0, simulation.Snapshot.NegativeResourceCount);
            Assert.All(simulation.Snapshot.ResourceUsage.Values, usage => Assert.Equal(0, usage));
            Assert.Equal(simulation.LedgerEntries.Count,
                simulation.LedgerEntries.Select(entry => entry.IdempotencyKey).Distinct().Count());
        }
    }
}
