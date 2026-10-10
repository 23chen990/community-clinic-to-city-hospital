using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

/// <summary>
/// M1 multi-day invariants for CURRENT §5.6 and §14.2.  The test uses the
/// actual FirstWhiteShift fixture and keeps the return probability configured
/// by that fixture so the 100 seeded runs exercise both observed outcomes.
/// </summary>
public sealed class MultiDaySimulationTests
{
    [Fact]
    public void OneHundredSeedsSurviveTwoContinuousWhiteShiftsWithStableSettlementAndLedgerInvariants()
    {
        var observedReturn = false;
        var observedNoReturn = false;
        var observedStrategies = new HashSet<LabDispatchPlan>();

        for (var seed = 1; seed <= 100; seed++)
        {
            var simulation = M1PlayScenarios.CreateFirstWhiteShift(seed);
            var dayOneUnfinished = new HashSet<string>(StringComparer.Ordinal);
            var dayOneReturned = new HashSet<string>(StringComparer.Ordinal);

            for (var day = 1; day <= 2; day++)
            {
                var strategy = StrategyFor(seed, day);
                var ledgerStart = simulation.LedgerEntries.Count;
                var eventsStart = simulation.Events.Count;
                var commandId = ((long)seed * 100) + day;

                RunOneRealWhiteShift(simulation, strategy, commandId, observedStrategies);
                AssertShiftInvariants(simulation, day, ledgerStart, eventsStart);

                var dayEvents = simulation.Events.Skip(eventsStart).ToArray();
                if (day == 1)
                {
                    foreach (var patientId in dayEvents
                                 .Where(entry => entry.EventType == SimulationEventType.PatientLeftUnfinished)
                                 .Select(entry => entry.PatientId))
                    {
                        dayOneUnfinished.Add(patientId);
                    }

                    AssertFutureDayPatientsRemainSpawned(simulation, futureDay: 2);
                    StartNextDay(simulation, commandId + 10_000);
                }
                else
                {
                    dayOneReturned.UnionWith(
                        simulation.Events
                            .Where(entry => entry.EventType == SimulationEventType.PatientReturnedNextDay)
                            .Select(entry => entry.PatientId));
                }

                AssertLedgerAndCashInvariant(
                    simulation,
                    M1PlayScenarios.FirstWhiteShiftConfig.InitialCashMinorUnits);
            }

            observedReturn |= dayOneUnfinished.Overlaps(dayOneReturned);
            observedNoReturn |= dayOneUnfinished.Any(patientId =>
                !dayOneReturned.Contains(patientId));

            Assert.Equal(2, simulation.Day);
            Assert.Equal(2, simulation.CompletedShifts.Count);
            Assert.Equal(2, Count(simulation, SimulationEventType.ShiftSettled));
        }

        Assert.True(observedReturn, "100 个种子中没有观察到次日回流患者。");
        Assert.True(observedNoReturn, "100 个种子中没有观察到未回流的未完成诊疗患者。");
        Assert.Contains(LabDispatchPlan.RiskPriority, observedStrategies);
        Assert.Contains(LabDispatchPlan.TemporarySupport, observedStrategies);
        Assert.Contains(LabDispatchPlan.OutsourceSamples, observedStrategies);
    }

    private static void RunOneRealWhiteShift(
        HospitalSimulation simulation,
        LabDispatchPlan strategy,
        long commandId,
        ISet<LabDispatchPlan> observedStrategies)
    {
        var config = M1PlayScenarios.FirstWhiteShiftConfig;
        var maxTicks = config.ShiftLengthTicks + config.ClosingDurationTicks +
            config.ClosingExtensionTicks + 20;
        var submitted = false;
        LabDispatchPlan? pendingAcceptedStrategy = null;

        for (var elapsed = 0; elapsed < maxTicks && !simulation.IsShiftComplete; elapsed++)
        {
            if (!submitted && strategy != LabDispatchPlan.None && CanSubmit(simulation.Snapshot, strategy))
            {
                Assert.Equal(
                    CommandResultStatus.Deferred,
                    simulation.Submit(SimulationCommand.DispatchLab(
                        commandId,
                        simulation.Tick,
                        strategy)).Status);
                submitted = true;
                pendingAcceptedStrategy = strategy;
            }

            simulation.AdvanceTick();
            if (pendingAcceptedStrategy is { } acceptedStrategy)
            {
                Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(commandId).Status);
                observedStrategies.Add(acceptedStrategy);
                pendingAcceptedStrategy = null;
            }
        }

        Assert.True(
            simulation.IsShiftComplete,
            $"seeded white shift did not settle by Tick {simulation.Tick} (day {simulation.Day}).");
    }

    private static bool CanSubmit(HospitalSnapshot snapshot, LabDispatchPlan strategy) => strategy switch
    {
        LabDispatchPlan.RiskPriority or LabDispatchPlan.TemporarySupport =>
            snapshot.Lab.DispatchPlan == LabDispatchPlan.None && snapshot.Lab.ResultBacklog > 0,
        LabDispatchPlan.OutsourceSamples =>
            snapshot.Lab.DispatchPlan == LabDispatchPlan.None &&
            snapshot.Patients.Any(patient =>
                patient.Stage == PatientStage.WaitingLabAnalysis &&
                patient.RiskBasisPoints < 3_500),
        _ => false,
    };

    private static LabDispatchPlan StrategyFor(int seed, int day)
    {
        return ((seed + day) % 4) switch
        {
            0 => LabDispatchPlan.None,
            1 => LabDispatchPlan.RiskPriority,
            2 => LabDispatchPlan.TemporarySupport,
            _ => LabDispatchPlan.OutsourceSamples,
        };
    }

    private static void AssertShiftInvariants(
        HospitalSimulation simulation,
        int expectedDay,
        int ledgerStart,
        int eventsStart)
    {
        Assert.Equal(expectedDay, simulation.Day);
        Assert.Equal(ShiftPhase.Settled, simulation.Phase);
        Assert.Equal(expectedDay, simulation.CompletedShifts.Count);
        Assert.Equal(expectedDay, Count(simulation, SimulationEventType.ShiftSettled));
        Assert.Equal(0, simulation.Snapshot.OrphanTaskCount);
        Assert.Equal(0, simulation.Snapshot.NegativeResourceCount);
        Assert.All(simulation.Snapshot.ResourceUsage.Values, usage => Assert.Equal(0, usage));
        Assert.Equal(0, simulation.Snapshot.PatientsInHospital);

        var dayEvents = simulation.Events.Skip(eventsStart).ToArray();
        var arrivals = dayEvents.Count(entry => entry.EventType is
            SimulationEventType.PatientEntered or SimulationEventType.PatientReturnedNextDay);
        var outcomes = dayEvents.Count(entry => entry.EventType is
            SimulationEventType.PatientDischarged or
            SimulationEventType.PatientTransferred or
            SimulationEventType.PatientLeftWithoutCare or
            SimulationEventType.PatientLeftUnfinished);
        var summary = simulation.CompletedShifts[^1].Summary;

        Assert.Equal(arrivals, summary.ArrivedPatients);
        Assert.Equal(arrivals, outcomes);
        Assert.Equal(
            dayEvents.Count(entry => entry.EventType == SimulationEventType.PatientDischarged),
            summary.DischargedPatients);
        Assert.Equal(
            dayEvents.Count(entry => entry.EventType == SimulationEventType.PatientTransferred),
            summary.TransferredPatients);
        Assert.Equal(
            dayEvents.Count(entry => entry.EventType == SimulationEventType.PatientLeftUnfinished),
            summary.UnfinishedPatients);
        Assert.Equal(
            dayEvents.Count(entry => entry.EventType == SimulationEventType.PatientLeftWithoutCare),
            summary.LeftWithoutCarePatients);

        var dayLedger = simulation.LedgerEntries.Skip(ledgerStart).ToArray();
        Assert.Equal(
            dayLedger.Where(entry => entry.Kind == LedgerKind.PatientRevenue).Sum(entry => entry.AmountMinorUnits),
            summary.RevenueMinorUnits);
        Assert.Equal(
            dayLedger.Where(entry => entry.Kind == LedgerKind.DispatchCost).Sum(entry => entry.AmountMinorUnits),
            summary.DispatchCostMinorUnits);

        // Once settled, repeated ticks cannot append a second settlement or
        // mutate the terminal resource state.
        var settledEvents = Count(simulation, SimulationEventType.ShiftSettled);
        var completedShifts = simulation.CompletedShifts.Count;
        var stateHash = simulation.StateHash;
        simulation.AdvanceTicks(5);
        Assert.Equal(settledEvents, Count(simulation, SimulationEventType.ShiftSettled));
        Assert.Equal(completedShifts, simulation.CompletedShifts.Count);
        Assert.Equal(stateHash, simulation.StateHash);
    }

    private static void AssertLedgerAndCashInvariant(HospitalSimulation simulation, long initialCash)
    {
        var ledger = simulation.LedgerEntries;
        Assert.Equal(ledger.Count, ledger.Select(entry => entry.IdempotencyKey).Distinct().Count());

        var patientRevenue = ledger
            .Where(entry => entry.Kind == LedgerKind.PatientRevenue)
            .ToArray();
        Assert.Equal(
            patientRevenue.Length,
            patientRevenue.Select(entry => entry.EntityId).Distinct(StringComparer.Ordinal).Count());

        var dischargedPatients = simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.PatientDischarged)
            .Select(entry => entry.PatientId)
            .OrderBy(patientId => patientId, StringComparer.Ordinal)
            .ToArray();
        var revenuePatients = patientRevenue
            .Select(entry => entry.EntityId)
            .OrderBy(patientId => patientId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(dischargedPatients, revenuePatients);

        var expectedCash = initialCash + ledger.Sum(entry => entry.Kind switch
        {
            LedgerKind.DispatchCost => -entry.AmountMinorUnits,
            _ => entry.AmountMinorUnits,
        });
        Assert.Equal(expectedCash, simulation.CashMinorUnits);
    }

    private static void AssertFutureDayPatientsRemainSpawned(
        HospitalSimulation simulation,
        int futureDay)
    {
        foreach (var arrival in M1PlayScenarios.FirstWhiteShiftArrivals.Where(arrival => arrival.Day == futureDay))
        {
            var patient = Assert.Single(
                simulation.Snapshot.Patients,
                candidate => candidate.PatientId == $"患者{arrival.EntityId:000}");
            Assert.Equal(PatientStage.Spawned, patient.Stage);
        }
    }

    private static void StartNextDay(HospitalSimulation simulation, long commandId)
    {
        Assert.Equal(
            CommandResultStatus.Deferred,
            simulation.Submit(new SimulationCommand(
                commandId,
                simulation.Tick,
                SimulationCommandType.ArrangeNextDay)).Status);
        simulation.AdvanceTick();
        Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(commandId).Status);
    }

    private static int Count(HospitalSimulation simulation, SimulationEventType eventType) =>
        simulation.Events.Count(entry => entry.EventType == eventType);
}
