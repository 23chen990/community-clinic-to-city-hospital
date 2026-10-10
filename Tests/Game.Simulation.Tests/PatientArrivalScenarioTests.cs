using Game.Simulation;
using Xunit;
namespace Game.Simulation.Tests;

// CURRENT §7.4, §7.8: explicit arrivals are deterministic content, not direct state mutations.
public sealed class PatientArrivalScenarioTests
{
    [Fact]
    public void ArrivalOrderIsIndependentOfInputCollectionOrder()
    {
        PatientArrival[] arrivals = [new(2, 10, "URTI"), new(1, 10, "DEHYDRATION")];
        var a = HospitalSimulation.CreateScenario(7, arrivals);
        var b = HospitalSimulation.CreateScenario(7, arrivals.Reverse());
        a.AdvanceTicks(10);
        b.AdvanceTicks(10);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(new[] {"患者001", "患者002"}, a.Events.Where(e => e.EventType == SimulationEventType.PatientEntered).Select(e => e.PatientId));
    }

    [Fact]
    public void InvalidArrivalContentFailsBeforeSimulationStarts()
    {
        Assert.Throws<ArgumentException>(() => HospitalSimulation.CreateScenario(7, [new(1, 1, "UNKNOWN")]));
        Assert.Throws<ArgumentException>(() => HospitalSimulation.CreateScenario(7, [new(1, 1, "URTI"), new(1, 2, "URTI")]));
        Assert.Throws<ArgumentException>(() => HospitalSimulation.CreateScenario(7, [new(1, -1, "URTI")]));
    }

    [Fact]
    public void ExplicitRiskAndPatienceCreateReproducibleSafetyScenarios()
    {
        var simulation = HospitalSimulation.CreateScenario(7, [new(1, 1, "HIGH_RISK_ABDOMINAL", 8_900, 7_000)]);
        var patient = Assert.Single(simulation.Snapshot.Patients);
        Assert.Equal(8_900, patient.RiskBasisPoints);
        Assert.Equal(7_000, patient.PatienceBasisPoints);
    }
}
