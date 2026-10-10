using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

// CURRENT §1.3, §3.1, §5.1, §5.3, §7.3: a deterministic first white shift
// makes the lab backlog visible before the player chooses a dispatch policy.
// The case mix and timing are reversible experiment parameters authorized by
// the product owner on 2026-10-08; they are not a second product baseline.
public sealed class DispatchTradeoffTests
{
    [Fact]
    public void DispatchComparisonUsesSixSpacedArrivalsAndAReal3600TickOperatingWindow()
    {
        var arrivals = M1PlayScenarios.DispatchComparisonArrivals;

        Assert.Equal(6, arrivals.Count);
        Assert.InRange(arrivals.Count, 1, 120);
        Assert.Equal(3_600, M1PlayScenarios.DispatchComparisonConfig.ShiftLengthTicks);
        Assert.True(arrivals.Select(arrival => arrival.ArrivalTick).Distinct().Count() >= 4);
        Assert.True(arrivals.Min(arrival => arrival.ArrivalTick) < 100);
        Assert.True(arrivals.Max(arrival => arrival.ArrivalTick) > 300);

        var simulation = M1PlayScenarios.CreateDispatchComparison(seed: M1PlayScenarios.Seed);
        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.ShiftTick >= M1PlayScenarios.DispatchComparisonConfig.ShiftLengthTicks,
            M1PlayScenarios.DispatchComparisonConfig.ShiftLengthTicks + 1));
        Assert.Equal(3_600, simulation.Snapshot.ShiftTick);
        Assert.Equal(6, simulation.Snapshot.ArrivedPatients);
    }

    [Fact]
    public void FirstWhiteShiftBuildsItsMainPressureInsideTheDefinedMiddleWindow()
    {
        var dayOne = M1PlayScenarios.FirstWhiteShiftDayOneArrivals;
        Assert.Equal(6, dayOne.Count);
        Assert.InRange(M1PlayScenarios.FirstWhiteShiftArrivals.Count, 6, 120);
        Assert.All(dayOne.Skip(1), arrival =>
            Assert.InRange(arrival.ArrivalTick, 720, 1_800));

        var simulation = M1PlayScenarios.CreateFirstWhiteShift(seed: M1PlayScenarios.Seed);
        // Tick 720 is the start of the accumulation phase. Check calm before
        // that boundary, then let the five mixed arrivals build the queue.
        simulation.AdvanceTicks(719);
        Assert.Equal(1, simulation.Snapshot.ArrivedPatients);
        Assert.Equal(0, simulation.Snapshot.Lab.ResultBacklog);

        var peakBacklog = 0;
        var peakTick = 0;
        while (simulation.Tick < M1PlayScenarios.MainPressureWindowEndTick)
        {
            simulation.AdvanceTick();
            if (simulation.Snapshot.Lab.ResultBacklog > peakBacklog)
            {
                peakBacklog = simulation.Snapshot.Lab.ResultBacklog;
                peakTick = simulation.Tick;
            }
        }

        Assert.InRange(peakTick,
            M1PlayScenarios.MainPressureWindowStartTick,
            M1PlayScenarios.MainPressureWindowEndTick);
        Assert.True(peakBacklog >= M1PlayScenarios.DispatchBacklogThreshold + 1);
        Assert.True(simulation.Snapshot.Lab.ResultBacklog >= M1PlayScenarios.DispatchBacklogThreshold);
        Assert.Equal(6, simulation.Snapshot.ArrivedPatients);
        Assert.Equal(5, simulation.Events.Count(entry =>
            entry.EventType == SimulationEventType.PatientEntered &&
            entry.Tick >= 720 && entry.Tick <= 1_800));
        Assert.Equal(6, simulation.Events.Count(entry =>
            entry.EventType == SimulationEventType.PatientEntered));
    }

    [Fact]
    public void FirstWhiteShiftDayTwoCombinesGuaranteedReturnWithNewPlannedDemand()
    {
        var config = M1PlayScenarios.FirstWhiteShiftConfig with
        {
            MinReturnChanceBasisPoints = 10_000,
            MaxReturnChanceBasisPoints = 10_000,
        };
        var simulation = M1PlayScenarios.CreateFirstWhiteShift(
            seed: M1PlayScenarios.Seed,
            config: config);

        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.ShiftComplete,
            config.ShiftLengthTicks + config.ClosingDurationTicks + 1));
        simulation.Submit(new SimulationCommand(
            99,
            simulation.Tick,
            SimulationCommandType.ArrangeNextDay));
        simulation.AdvanceTick();

        Assert.Equal(2, simulation.Day);
        Assert.True(simulation.AdvanceUntil(
            snapshot => simulation.Events.Any(entry =>
                entry.EventType == SimulationEventType.PatientReturnedNextDay),
            config.ReturnArrivalDelayTicks + 2));
        Assert.Contains(simulation.Events, entry =>
            entry.EventType == SimulationEventType.PatientReturnedNextDay);
        Assert.True(simulation.AdvanceUntil(
            snapshot => simulation.Events.Any(entry =>
                entry.EventType == SimulationEventType.PatientEntered &&
                entry.PatientId == "患者007"),
            800));
        Assert.Contains(simulation.Events, entry =>
            entry.EventType == SimulationEventType.PatientEntered &&
            entry.PatientId == "患者007");
    }

    [Fact]
    public void RiskPriorityChangesWhoGetsTheFirstResultAtTheSameLabCapacity()
    {
        var normal = PrepareAtBacklog(LabDispatchPlan.None);
        var prioritized = PrepareAtBacklog(LabDispatchPlan.RiskPriority);
        var pendingAtDecision = prioritized.Simulation.Snapshot.Patients
            .Where(patient => patient.Stage == PatientStage.ResultPending)
            .OrderByDescending(patient => patient.RiskBasisPoints)
            .ThenBy(patient => patient.PatientId, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(pendingAtDecision);

        var normalFirst = AdvanceToNextPublished(normal.Simulation, normal.ResultPublishedCount);
        var priorityFirst = AdvanceToNextPublished(prioritized.Simulation, prioritized.ResultPublishedCount);

        Assert.Equal(normalFirst.Tick, priorityFirst.Tick);
        Assert.Equal(pendingAtDecision[0].PatientId, priorityFirst.PatientId);
        Assert.NotEqual(normalFirst.PatientId, priorityFirst.PatientId);
        Assert.Equal(normal.Simulation.Snapshot.Lab.TechnicianFatigueBasisPoints,
            prioritized.Simulation.Snapshot.Lab.TechnicianFatigueBasisPoints);
    }

    [Fact]
    public void TemporarySupportClearsSeveralPendingResultsAndLeavesFatigue()
    {
        var prepared = PrepareAtBacklog(LabDispatchPlan.TemporarySupport);
        var before = prepared.BeforeDispatch;
        var dispatchTick = prepared.Simulation.Tick;
        var duration = M1PlayScenarios.DispatchComparisonConfig.TemporarySupportDurationTicks;

        prepared.Simulation.AdvanceTicks(duration);

        var publishedDuringSupport = prepared.Simulation.Events.Count(entry =>
            entry.EventType == SimulationEventType.ResultPublished &&
            entry.Tick > dispatchTick && entry.Tick <= dispatchTick + duration);
        Assert.True(publishedDuringSupport >= 2);
        Assert.Contains(prepared.Simulation.Events, entry =>
            entry.EventType == SimulationEventType.TemporarySupportStarted);
        Assert.True(prepared.Simulation.Snapshot.Lab.ResultBacklog < before.Lab.ResultBacklog);
        Assert.True(prepared.Simulation.Snapshot.Lab.TechnicianFatigueBasisPoints >
            before.Lab.TechnicianFatigueBasisPoints);
        Assert.Contains(prepared.Simulation.Events, entry =>
            entry.EventType == SimulationEventType.TemporarySupportEnded);
    }

    [Fact]
    public void OutsourcingChargesMoneyAndFreesOnlyOrdinaryUnanalyzedSamplesBeforeASlowerReturn()
    {
        var prepared = PrepareAtBacklog(LabDispatchPlan.OutsourceSamples);
        var before = prepared.BeforeDispatch;
        var config = M1PlayScenarios.DispatchComparisonConfig;

        var started = prepared.Simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.OutsourcedSampleStarted)
            .ToArray();
        Assert.Equal(config.MaxOutsourceSamples, started.Length);
        Assert.All(started, entry =>
        {
            var patient = before.Patients.Single(snapshot => snapshot.PatientId == entry.PatientId);
            Assert.Equal(PatientStage.WaitingLabAnalysis, patient.Stage);
            Assert.True(patient.RiskBasisPoints < 3_500);
        });
        Assert.Equal(config.MaxOutsourceSamples * config.OutsourceCostPerSampleMinorUnits,
            prepared.Simulation.LedgerEntries
                .Where(entry => entry.Kind == LedgerKind.DispatchCost)
                .Sum(entry => entry.AmountMinorUnits));
        Assert.True(prepared.Simulation.Snapshot.Lab.AnalysisQueue <=
            before.Lab.AnalysisQueue - config.MaxOutsourceSamples);
        Assert.Equal(config.MaxOutsourceSamples, prepared.Simulation.Snapshot.Lab.OutsourcedSamples);

        var firstStarted = started.Min(entry => entry.Tick);
        Assert.True(prepared.Simulation.AdvanceUntil(
            snapshot => snapshot.Lab.OutsourcedSamples == 0,
            config.OutsourceReturnTicks + 2));
        var firstReturned = prepared.Simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.OutsourcedResultReturned)
            .Min(entry => entry.Tick);

        Assert.Equal(config.OutsourceReturnTicks, firstReturned - firstStarted);
        Assert.True(config.OutsourceReturnTicks > config.LabAnalysisServiceTicks);
    }

    private static PreparedDispatch PrepareAtBacklog(LabDispatchPlan plan)
    {
        var simulation = M1PlayScenarios.CreateDispatchComparison(seed: M1PlayScenarios.Seed);
        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.Lab.ResultBacklog >= M1PlayScenarios.DispatchBacklogThreshold,
            M1PlayScenarios.DispatchComparisonConfig.ShiftLengthTicks));

        var beforeDispatch = simulation.Snapshot;
        var resultPublishedCount = simulation.Events.Count(entry =>
            entry.EventType == SimulationEventType.ResultPublished);
        if (plan != LabDispatchPlan.None)
        {
            var command = SimulationCommand.DispatchLab(1, simulation.Tick, plan);
            Assert.Equal(CommandResultStatus.Deferred, simulation.Submit(command).Status);
            simulation.AdvanceTick();
            Assert.Equal(CommandResultStatus.Accepted, simulation.GetCommandResult(command.CommandId).Status);
        }

        return new PreparedDispatch(simulation, resultPublishedCount, beforeDispatch);
    }

    private static SimulationEvent AdvanceToNextPublished(
        HospitalSimulation simulation,
        int resultPublishedCount)
    {
        Assert.True(simulation.AdvanceUntil(
            snapshot => simulation.Events.Count(entry => entry.EventType == SimulationEventType.ResultPublished) >
                resultPublishedCount,
            M1PlayScenarios.DispatchComparisonConfig.ShiftLengthTicks));
        return simulation.Events
            .Where(entry => entry.EventType == SimulationEventType.ResultPublished)
            .Skip(resultPublishedCount)
            .First();
    }

    private sealed record PreparedDispatch(
        HospitalSimulation Simulation,
        int ResultPublishedCount,
        HospitalSnapshot BeforeDispatch);
}
