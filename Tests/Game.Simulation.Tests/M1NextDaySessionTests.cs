using Game.Simulation;
using Game.Simulation.Demo;
using Xunit;
namespace Game.Simulation.Tests;
public sealed class M1NextDaySessionTests
{
    [Fact]
    public void SessionShowsUnfinishedCareAndLetsTheSamePatientReturnNextDay()
    {
        var config = new M1SimulationConfig {
            ShiftLengthTicks=100, ClosingDurationTicks=20, ServiceTicks=10, LabAnalysisServiceTicks=200,
            NormalPublishIntervalTicks=500, RiskIncreasePerWaitingTick=0, PatienceLossPerWaitingTick=0,
            MinReturnChanceBasisPoints=10000,MaxReturnChanceBasisPoints=10000,ReturnArrivalDelayTicks=1
        };
        var simulation = HospitalSimulation.CreateScenario(7,[new(1,1,"URTI",1000)],config);
        using var input = new StringReader("a\na\na\nd\nq\n");
        using var output = new StringWriter();
        M1SimulationSession.Run(input,output,simulation);
        var transcript=output.ToString();
        Assert.Contains("未完成诊疗",transcript);
        Assert.Contains("第2日",transcript);
        Assert.Contains("患者再次到院",transcript);
        Assert.DoesNotContain("本班患者已完成诊疗并离院",transcript);
        Assert.Empty(simulation.LedgerEntries);
    }
}
