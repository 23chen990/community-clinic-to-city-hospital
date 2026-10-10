using Game.Simulation;
using Game.Simulation.Demo;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class M1SimulationSessionTests
{
    [Fact]
    public void SessionShowsTheRealLabBacklogDispatchAndShiftSettlement()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 7);
        using var input = new StringReader("a\ns\na\na\nq\n");
        using var output = new StringWriter();

        M1SimulationSession.Run(input, output, simulation);

        var transcript = output.ToString();
        Assert.Contains("排队患者", transcript);
        Assert.Contains("结果发布积压：4 份", transcript);
        Assert.Contains("临时支援开始", transcript);
        Assert.Contains(simulation.Events, entry => entry.Status == "结果返回门诊");
        Assert.Contains("班次结算", transcript);
        Assert.Contains("本班收入", transcript);
    }

    // CURRENT §9.5: the sandbox must expose an actual eligible sample before
    // fast-forwarding beyond its outsourcing opportunity.
    [Fact]
    public void AdvanceStopsWhileAnOrdinaryUnanalyzedSampleCanBeOutsourced()
    {
        var simulation = HospitalSimulation.CreateScenario(10,
        [
            new PatientArrival(1, 1, "URTI", 1_000),
            new PatientArrival(2, 1, "URTI", 1_000),
            new PatientArrival(3, 1, "URTI", 1_000),
        ], new M1SimulationConfig
        {
            RiskIncreasePerWaitingTick = 0,
            PatienceLossPerWaitingTick = 0,
            NormalPublishIntervalTicks = 5_000,
        });
        using var input = new StringReader("a\no\nq\n");
        using var output = new StringWriter();

        M1SimulationSession.Run(input, output, simulation);

        Assert.Contains("Accepted：已外送", output.ToString());
        Assert.Contains(simulation.Events, entry =>
            entry.EventType == SimulationEventType.OutsourcedSampleStarted);
        Assert.False(simulation.IsShiftComplete);
    }

    // CURRENT §5.4: reaching Closing must leave a chance to extend once for
    // High/Critical patients before the hard closing limit transfers them.
    [Fact]
    public void AdvanceExposesClosingBeforeTheHighRiskExtensionWindowEnds()
    {
        var simulation = HospitalSimulation.CreateScenario(11,
            [new PatientArrival(1, 1, "HIGH_RISK_ABDOMINAL", 7_000)],
            new M1SimulationConfig
            {
                ShiftLengthTicks = 100,
                ClosingDurationTicks = 20,
                ClosingExtensionTicks = 10,
                ServiceTicks = 500,
                RiskIncreasePerWaitingTick = 0,
                PatienceLossPerWaitingTick = 0,
                ReturnArrivalDelayTicks = 1,
            });
        using var input = new StringReader("a\ne\nq\n");
        using var output = new StringWriter();

        M1SimulationSession.Run(input, output, simulation);

        Assert.Contains("Accepted：延长收尾已生效", output.ToString());
        Assert.Equal(ShiftPhase.Closing, simulation.Phase);
        Assert.DoesNotContain(simulation.Events, entry =>
            entry.EventType == SimulationEventType.PatientTransferred);
    }
}
