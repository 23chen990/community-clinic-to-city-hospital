using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class ConsultationSchedulingTests
{
    [Fact]
    public void SharedDoctorUsesTwoInitialConsultationsThenOneFollowUpWhileBothQueuesWait()
    {
        var simulation = CreateConsultationScenario(
            new PatientArrival(1, 1, "URTI", 1_000),
            new PatientArrival(2, 1, "URTI", 1_000),
            new PatientArrival(3, 2, "URTI", 1_000));

        var consultationStarts = AdvanceUntilConsultationStarts(simulation, 4);

        Assert.Equal(SimulationEventType.ConsultationStarted, consultationStarts[0].EventType);
        Assert.Equal(SimulationEventType.ConsultationStarted, consultationStarts[1].EventType);
        Assert.Equal(SimulationEventType.FollowUpStarted, consultationStarts[3].EventType);
        Assert.Equal("患者001", consultationStarts[0].PatientId);
        Assert.Equal("患者002", consultationStarts[1].PatientId);
        Assert.Equal("患者001", consultationStarts[3].PatientId);
    }

    [Fact]
    public void HighRiskFollowUpWaitsForActiveConsultationThenPrecedesWaitingInitialConsultation()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 91,
            arrivals: new[]
            {
                new PatientArrival(1, 1, "DEHYDRATION", 1_000),
                new PatientArrival(2, 1, "HIGH_RISK_ABDOMINAL", 9_000),
                new PatientArrival(3, 1, "URTI", 1_000),
                new PatientArrival(4, 1, "URTI", 1_000),
                new PatientArrival(5, 1, "URTI", 1_000),
            },
            config: new M1SimulationConfig
            {
                ServiceTicks = 3,
                TreatmentServiceTicks = 1,
                LabAnalysisServiceTicks = 1,
                NormalPublishIntervalTicks = 1,
                PatientPatienceBasisPoints = 10_000,
                PatienceLossPerWaitingTick = 0,
                RiskIncreasePerWaitingTick = 0,
                CriticalTransferWaitTicks = 10_000,
            });

        var resultReturn = AdvanceUntilEvent(
            simulation,
            entry => entry.EventType == SimulationEventType.ResultReturnedToConsultation &&
                     entry.PatientId == "患者002");
        var startsAfterReturn = AdvanceUntilEvents(
            simulation,
            entry => entry.EventType is SimulationEventType.ConsultationStarted or SimulationEventType.FollowUpStarted,
            count: 2);

        Assert.Contains(
            simulation.Events,
            entry => entry.EventType == SimulationEventType.ConsultationStarted &&
                     entry.PatientId == "患者004" &&
                     entry.Tick <= resultReturn.Tick);
        Assert.Contains(
            simulation.Events,
            entry => entry.EventType == SimulationEventType.ConsultationCompleted &&
                     entry.PatientId == "患者004" &&
                     entry.Tick > resultReturn.Tick);
        Assert.Equal(SimulationEventType.FollowUpStarted, startsAfterReturn[0].EventType);
        Assert.Equal("患者002", startsAfterReturn[0].PatientId);
        Assert.Equal(SimulationEventType.ConsultationStarted, startsAfterReturn[1].EventType);
        Assert.Equal("患者005", startsAfterReturn[1].PatientId);
        Assert.True(startsAfterReturn[0].Tick > resultReturn.Tick);
    }

    [Fact]
    public void OrdinaryFollowUpIsServedBeforeInitialQueueDrains()
    {
        var simulation = CreateConsultationScenario(
            new PatientArrival(1, 1, "URTI", 1_000),
            new PatientArrival(2, 1, "URTI", 1_000),
            new PatientArrival(3, 1, "URTI", 1_000),
            new PatientArrival(4, 1, "URTI", 1_000),
            new PatientArrival(5, 1, "URTI", 1_000),
            new PatientArrival(6, 30, "URTI", 1_000),
            new PatientArrival(7, 31, "URTI", 1_000));

        var followUp = AdvanceUntilEvent(
            simulation,
            entry => entry.EventType == SimulationEventType.FollowUpStarted);

        Assert.Contains(
            simulation.Snapshot.Patients,
            patient => patient.Stage == PatientStage.WaitingConsult);
        Assert.Contains(
            simulation.Events,
            entry => entry.EventType == SimulationEventType.ConsultationStarted &&
                     entry.Tick < followUp.Tick);
    }

    [Fact]
    public void EqualArrivalOrderIsStableByNumericIdAcrossReplays()
    {
        var arrivals = new[]
        {
            new PatientArrival(30, 1, "URTI", 1_000),
            new PatientArrival(10, 1, "URTI", 1_000),
            new PatientArrival(20, 1, "URTI", 1_000),
        };
        var first = CreateConsultationScenario(arrivals);
        var second = CreateConsultationScenario(arrivals);

        var firstStarts = AdvanceUntilEvents(
            first,
            entry => entry.EventType == SimulationEventType.ConsultationStarted,
            count: 3);
        var secondStarts = AdvanceUntilEvents(
            second,
            entry => entry.EventType == SimulationEventType.ConsultationStarted,
            count: 3);

        Assert.Equal(firstStarts.Select(entry => entry.PatientId), secondStarts.Select(entry => entry.PatientId));
        Assert.Equal(new[] { "患者010", "患者020", "患者030" }, firstStarts.Select(entry => entry.PatientId));
    }

    private static HospitalSimulation CreateConsultationScenario(params PatientArrival[] arrivals) =>
        HospitalSimulation.CreateScenario(
            seed: 91,
            arrivals: arrivals,
            config: new M1SimulationConfig
            {
                ServiceTicks = 1,
                TreatmentServiceTicks = 1,
                LabAnalysisServiceTicks = 1,
                NormalPublishIntervalTicks = 1,
                PatientPatienceBasisPoints = 10_000,
                PatienceLossPerWaitingTick = 0,
                RiskIncreasePerWaitingTick = 0,
                CriticalTransferWaitTicks = 10_000,
            });

    private static SimulationEvent AdvanceUntilEvent(
        HospitalSimulation simulation,
        Func<SimulationEvent, bool> predicate,
        int maxTicks = 5_000)
    {
        for (var index = 0; index < maxTicks; index++)
        {
            var before = simulation.Events.Count;
            simulation.AdvanceTick();
            var match = simulation.Events.Skip(before).FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException("在限定 Tick 内未观察到目标调度事件");
    }

    private static IReadOnlyList<SimulationEvent> AdvanceUntilEvents(
        HospitalSimulation simulation,
        Func<SimulationEvent, bool> predicate,
        int count,
        int maxTicks = 5_000)
    {
        var matches = new List<SimulationEvent>();
        for (var index = 0; index < maxTicks && matches.Count < count; index++)
        {
            var before = simulation.Events.Count;
            simulation.AdvanceTick();
            matches.AddRange(simulation.Events.Skip(before).Where(predicate));
        }

        Assert.Equal(count, matches.Count);
        return matches;
    }

    private static IReadOnlyList<SimulationEvent> AdvanceUntilConsultationStarts(
        HospitalSimulation simulation,
        int count,
        int maxTicks = 5_000) =>
        AdvanceUntilEvents(
            simulation,
            entry => entry.EventType is SimulationEventType.ConsultationStarted or SimulationEventType.FollowUpStarted,
            count,
            maxTicks);
}
