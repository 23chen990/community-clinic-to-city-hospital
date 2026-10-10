using Game.Simulation;
using Xunit;
namespace Game.Simulation.Tests;

// CURRENT §5.6: already completed work survives repeat unfinished visits.
public sealed class ReturnWorkProgressTests
{
    [Fact]
    public void WaitingForAWorkstationOnDayTwoDoesNotEraseDayOneAnalysisProgress()
    {
        var simulation = HospitalSimulation.CreateScenario(7,
            [new(1,1,"URTI",1000),new(2,1,"URTI",1000,Day:2)],
            new M1SimulationConfig {
                ShiftLengthTicks=100,ClosingDurationTicks=20,ServiceTicks=2,TreatmentServiceTicks=2,
                LabAnalysisServiceTicks=200,NormalPublishIntervalTicks=20,
                MinReturnChanceBasisPoints=10000,MaxReturnChanceBasisPoints=10000,
                ReturnArrivalDelayTicks=20,RiskIncreasePerWaitingTick=0,PatienceLossPerWaitingTick=0
            });
        for(var day=1;day<=2;day++) {
            Assert.True(simulation.AdvanceUntil(s=>s.ShiftComplete,120));
            simulation.Submit(new SimulationCommand(day,simulation.Tick,SimulationCommandType.ArrangeNextDay));
            simulation.AdvanceTick();
        }
        Assert.True(simulation.AdvanceUntil(s=>s.ShiftComplete,120));
        Assert.Equal(PatientStage.Completed,simulation.Snapshot.Patients.Single(p=>p.PatientId=="患者001").Stage);
        Assert.Single(simulation.LedgerEntries,e=>e.EntityId=="患者001"&&e.Kind==LedgerKind.PatientRevenue);
    }
}
