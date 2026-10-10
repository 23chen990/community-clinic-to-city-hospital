using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class PatientReturnContinuityTests
{
    [Fact]
    public void ResultPendingReturnKeepsAnalyzedResultWithoutResampling()
    {
        var config = ContinuityConfig() with
        {
            NormalPublishIntervalTicks = 500,
            TemporarySupportPublishIntervalTicks = 1,
            LabAnalysisServiceTicks = 10,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1201,
            arrivals: new[] { new PatientArrival(1, 1, "URTI", 1000) },
            config: config);

        AdvanceUntil(simulation, current => current.Snapshot.Lab.ResultBacklog > 0);
        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);
        Assert.Equal(1, Count(simulation, SimulationEventType.LabSamplingStarted));
        Assert.Equal(1, Count(simulation, SimulationEventType.LabAnalysisStarted));

        StartNextDay(simulation);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientReturnedNextDay);
        Assert.Equal(PatientStage.ResultPending, FindPatient(simulation, 1).Stage);

        var command = SimulationCommand.DispatchLab(
            commandId: 1202,
            tick: simulation.Tick,
            plan: LabDispatchPlan.TemporarySupport);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);
        AdvanceUntilEvent(simulation, SimulationEventType.ResultPublished);

        Assert.Equal(1, Count(simulation, SimulationEventType.LabSamplingStarted));
        Assert.Equal(1, Count(simulation, SimulationEventType.LabAnalysisStarted));
        Assert.Equal(1, Count(simulation, SimulationEventType.LabAnalysisCompleted));
        Assert.Equal(1, Count(simulation, SimulationEventType.ResultPublished));
    }

    [Fact]
    public void InterruptedPharmacyVisitReturnsWithoutRepeatingDiagnosisOrTreatment()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1203,
            arrivals: new[] { new PatientArrival(1, 1, "DEHYDRATION", 1000) },
            config: ContinuityConfig() with
            {
                ServiceTicks = 10,
                TreatmentServiceTicks = 85,
            });

        AdvanceUntilEvent(simulation, SimulationEventType.PharmacyStarted);
        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);

        StartNextDay(simulation);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientReturnedNextDay);
        Assert.Equal(PatientStage.WaitingPharmacy, FindPatient(simulation, 1).Stage);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientDischarged, maxTicks: 300);

        Assert.Equal(1, Count(simulation, SimulationEventType.ConsultationStarted));
        Assert.Equal(1, Count(simulation, SimulationEventType.TreatmentStarted));
        Assert.Equal(1, Count(simulation, SimulationEventType.TreatmentCompleted));
        Assert.Equal(2, Count(simulation, SimulationEventType.PharmacyStarted));
        Assert.Equal(1, Count(simulation, SimulationEventType.PatientDischarged));
    }

    [Fact]
    public void OutsourcedResultReturnDoesNotChargeDispatchTwice()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1205,
            arrivals: new[]
            {
                new PatientArrival(1, 1, "URTI", 1000),
                new PatientArrival(2, 2, "URTI", 1000),
            },
            config: ContinuityConfig() with
            {
                LabAnalysisServiceTicks = 200,
                OutsourceReturnTicks = 130,
                OutsourceCostPerSampleMinorUnits = 40,
            });

        AdvanceUntil(simulation, current => current.Snapshot.Lab.AnalysisQueue > 0);
        var dispatch = SimulationCommand.DispatchLab(
            commandId: 1206,
            tick: simulation.Tick,
            plan: LabDispatchPlan.OutsourceSamples);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(dispatch).Status);
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(1206).Status);

        var dispatchEntries = simulation.LedgerEntries
            .Where(entry => entry.Kind == LedgerKind.DispatchCost)
            .ToArray();
        Assert.Single(dispatchEntries);

        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);
        StartNextDay(simulation);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientReturnedNextDay, maxTicks: 100);
        AdvanceUntilEvent(simulation, SimulationEventType.OutsourcedResultReturned, maxTicks: 200);

        var entriesAfterReturn = simulation.LedgerEntries
            .Where(entry => entry.Kind == LedgerKind.DispatchCost)
            .ToArray();
        Assert.Single(entriesAfterReturn);
        Assert.Equal(dispatchEntries[0].AmountMinorUnits, entriesAfterReturn[0].AmountMinorUnits);
        Assert.Equal(1, Count(simulation, SimulationEventType.OutsourcedSampleStarted));
    }

    [Fact]
    public void OrdinaryPatientWithZeroReturnChanceDoesNotComeBack()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1207,
            arrivals: new[] { new PatientArrival(1, 1, "URTI", 1000) },
            config: ContinuityConfig() with
            {
                LabAnalysisServiceTicks = 200,
                MinReturnChanceBasisPoints = 0,
                MaxReturnChanceBasisPoints = 0,
            });

        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);
        StartNextDay(simulation);
        for (var i = 0; i < 100; i++)
        {
            simulation.AdvanceTick();
        }

        Assert.Equal(0, Count(simulation, SimulationEventType.PatientReturnedNextDay));
        Assert.Equal(PatientStage.LeftUnfinished, FindPatient(simulation, 1).Stage);
        Assert.DoesNotContain(simulation.LedgerEntries, entry => entry.Kind == LedgerKind.PatientRevenue);
    }

    [Fact]
    public void HighRiskPatientWhosePatienceExpiresTransfersAtClosingWithoutReturning()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1215,
            arrivals: new[] { new PatientArrival(1, 1, "HIGH_RISK_ABDOMINAL", 7_000, 1) },
            config: ContinuityConfig() with
            {
                ShiftLengthTicks = 10,
                ClosingDurationTicks = 10,
                ServiceTicks = 100,
                PatienceLossPerWaitingTick = 1,
                RiskIncreasePerWaitingTick = 0,
                CriticalTransferWaitTicks = 10_000,
            });

        AdvanceUntil(simulation, current => current.IsShiftComplete, maxTicks: 30);

        var patient = FindPatient(simulation, 1);
        Assert.Equal(RiskLevel.High, patient.RiskLevel);
        Assert.Equal(PatientStage.Transferred, patient.Stage);
        Assert.Equal(1, Count(simulation, SimulationEventType.PatientTransferred));
        Assert.Equal(0, Count(simulation, SimulationEventType.PatientLeftWithoutCare));

        StartNextDay(simulation);
        for (var i = 0; i < 20; i++)
        {
            simulation.AdvanceTick();
        }

        Assert.Equal(0, Count(simulation, SimulationEventType.PatientReturnedNextDay));
    }

    [Fact]
    public void UnfinishedOutsourcedSampleUsesExternalLabAsClosingRootCause()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1217,
            arrivals: new[]
            {
                new PatientArrival(1, 1, "URTI", 1_000),
                new PatientArrival(2, 2, "URTI", 1_000),
            },
            config: ContinuityConfig() with
            {
                LabAnalysisServiceTicks = 200,
                OutsourceReturnTicks = 2_000,
            });

        AdvanceUntil(simulation, current => current.Snapshot.Lab.AnalysisQueue > 0);
        var command = SimulationCommand.DispatchLab(
            commandId: 1218,
            tick: simulation.Tick,
            plan: LabDispatchPlan.OutsourceSamples);
        Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(command.CommandId).Status);

        var outsourcedPatientId = simulation.Events
            .Last(entry => entry.EventType == SimulationEventType.OutsourcedSampleStarted)
            .PatientId;
        AdvanceUntil(simulation, current => current.IsShiftComplete, maxTicks: 200);

        Assert.Contains(simulation.RootCauseEdges, edge =>
            edge.CauseEntity == "基础化验.外送" &&
            edge.EffectEntity == outsourcedPatientId &&
            edge.ReasonCode == RootCauseCode.ShiftClosingLimit);
    }

    [Fact]
    public void ReturnedPatientCompletesTreatmentAndProducesRevenueOnce()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1209,
            arrivals: new[] { new PatientArrival(1, 1, "DEHYDRATION", 1000) },
            config: ContinuityConfig() with
            {
                ServiceTicks = 10,
                TreatmentServiceTicks = 85,
            });

        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);
        StartNextDay(simulation);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientReturnedNextDay);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientDischarged, maxTicks: 300);

        var revenue = simulation.LedgerEntries
            .Where(entry => entry.Kind == LedgerKind.PatientRevenue)
            .ToArray();
        Assert.Single(revenue);
        Assert.Equal(1, Count(simulation, SimulationEventType.PatientDischarged));
        Assert.Equal(1, Count(simulation, SimulationEventType.TreatmentCompleted));
    }

    [Fact]
    public void InterruptedLabAnalysisUsesRemainingDurationAfterReturn()
    {
        var config = ContinuityConfig() with
        {
            ServiceTicks = 5,
            LabAnalysisServiceTicks = 40,
        };
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1211,
            arrivals: new[] { new PatientArrival(1, 80, "URTI", 1000) },
            config: config);

        AdvanceUntilEvent(simulation, SimulationEventType.LabAnalysisStarted, maxTicks: 150);
        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);
        StartNextDay(simulation);
        AdvanceUntilEvent(simulation, SimulationEventType.PatientReturnedNextDay, maxTicks: 100);

        var startsBeforeResume = Count(simulation, SimulationEventType.LabAnalysisStarted);
        var resumedStart = AdvanceUntilEvent(
            simulation,
            SimulationEventType.LabAnalysisStarted,
            maxTicks: 200,
            minimumCount: startsBeforeResume);
        Assert.Equal(startsBeforeResume + 1, Count(simulation, SimulationEventType.LabAnalysisStarted));

        var completed = AdvanceUntilEvent(
            simulation,
            SimulationEventType.LabAnalysisCompleted,
            maxTicks: 200);
        var resumedDuration = completed.Tick - resumedStart.Tick;
        Assert.InRange(resumedDuration, 1, config.LabAnalysisServiceTicks - 1);
    }

    [Fact]
    public void DayTwoArrivalAndSummaryDoNotRepeatPriorDayRevenue()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 1213,
            arrivals: new[]
            {
                new PatientArrival(1, 1, "URTI", 1000, Day: 1),
                new PatientArrival(2, 1, "DEHYDRATION", 1000, Day: 2),
            },
            config: ContinuityConfig() with
            {
                LabAnalysisServiceTicks = 200,
                MinReturnChanceBasisPoints = 0,
                MaxReturnChanceBasisPoints = 0,
            });

        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);
        StartNextDay(simulation);
        AdvanceUntil(simulation, snapshot => snapshot.IsShiftComplete, maxTicks: 200);

        Assert.Equal(2, simulation.CompletedShifts.Count);
        Assert.Equal(1, simulation.CompletedShifts[0].Summary.ArrivedPatients);
        Assert.Equal(1, simulation.CompletedShifts[1].Summary.ArrivedPatients);
        Assert.Equal(0, simulation.CompletedShifts[0].Summary.RevenueMinorUnits);
        Assert.Equal(90, simulation.CompletedShifts[1].Summary.RevenueMinorUnits);
        Assert.Single(simulation.LedgerEntries, entry => entry.Kind == LedgerKind.PatientRevenue);
    }

    private static M1SimulationConfig ContinuityConfig()
    {
        return new M1SimulationConfig
        {
            ShiftLengthTicks = 100,
            ClosingDurationTicks = 20,
            ClosingExtensionTicks = 10,
            ServiceTicks = 1,
            TreatmentServiceTicks = 4,
            LabAnalysisServiceTicks = 20,
            NormalPublishIntervalTicks = 20,
            TemporarySupportPublishIntervalTicks = 1,
            TemporarySupportDurationTicks = 100,
            OutsourceReturnTicks = 20,
            OutsourceCostPerSampleMinorUnits = 40,
            ReturnArrivalDelayTicks = 1,
            MinReturnChanceBasisPoints = 10_000,
            MaxReturnChanceBasisPoints = 10_000,
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
            CriticalTransferWaitTicks = 10_000,
            InitialCashMinorUnits = 1_000,
        };
    }

    private static PatientSnapshot FindPatient(HospitalSimulation simulation, long entityId)
    {
        return Assert.Single(simulation.Snapshot.Patients, patient => patient.PatientId == $"患者{entityId:000}");
    }

    private static int Count(HospitalSimulation simulation, SimulationEventType eventType)
    {
        return simulation.Events.Count(entry => entry.EventType == eventType);
    }

    private static void StartNextDay(HospitalSimulation simulation)
    {
        var commandId = 90_000 + simulation.CompletedShifts.Count;
        Assert.Equal(
            CommandResultStatus.Deferred,
            simulation.Submit(new SimulationCommand(
                commandId,
                simulation.Tick,
                SimulationCommandType.ArrangeNextDay)).Status);
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(commandId).Status);
    }

    private static void AdvanceUntil(
        HospitalSimulation simulation,
        Func<HospitalSimulation, bool> predicate,
        int maxTicks = 2_000)
    {
        for (var i = 0; i < maxTicks && !predicate(simulation); i++)
        {
            simulation.AdvanceTick();
        }

        Assert.True(predicate(simulation), $"Condition was not reached by tick {simulation.Tick}.");
    }

    private static SimulationEvent AdvanceUntilEvent(
        HospitalSimulation simulation,
        SimulationEventType eventType,
        int maxTicks = 2_000,
        int minimumCount = 0)
    {
        var existing = simulation.Events
            .Where(entry => entry.EventType == eventType)
            .ToArray();
        if (existing.Length > minimumCount)
        {
            return existing[^1];
        }

        for (var i = 0; i < maxTicks; i++)
        {
            var before = simulation.Events.Count;
            simulation.AdvanceTick();
            var newEvent = simulation.Events
                .Skip(before)
                .FirstOrDefault(entry => entry.EventType == eventType);
            if (newEvent is not null)
            {
                return newEvent;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Event {eventType} was not emitted by tick {simulation.Tick}.");
    }
}
