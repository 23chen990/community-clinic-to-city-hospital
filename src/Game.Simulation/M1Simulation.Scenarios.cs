namespace Game.Simulation;

/// <summary>Deterministic content input; CURRENT §7.3, §7.4, §7.8.</summary>
public sealed record PatientArrival(
    long EntityId,
    int ArrivalTick,
    string CaseId,
    int? RiskBasisPoints = null,
    int? PatienceBasisPoints = null,
    int Day = 1,
    PatientArrivalKind Kind = PatientArrivalKind.Ordinary);

public enum PatientArrivalKind { Ordinary, Story, Urgent }

public sealed partial class HospitalSimulation
{
    public static HospitalSimulation CreateScenario(
        int seed,
        IEnumerable<PatientArrival> arrivals,
        M1SimulationConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        var plan = arrivals.ToArray();
        if (plan.Any(arrival => arrival is null || arrival.EntityId <= 0 || arrival.ArrivalTick < 1 || arrival.Day < 1 || !Enum.IsDefined(arrival.Kind) ||
            arrival.RiskBasisPoints is < 0 or > 10_000 || arrival.PatienceBasisPoints is < 0 or > 10_000) ||
            plan.Select(arrival => arrival.EntityId).Distinct().Count() != plan.Length)
        {
            throw new ArgumentException("Arrival IDs must be unique and positive, ticks positive, and values within 0–10000.", nameof(arrivals));
        }

        var simulation = new HospitalSimulation(seed, config);
        foreach (var arrival in plan.OrderBy(arrival => arrival.EntityId))
        {
            simulation.plannedAdmissionDays[arrival.EntityId] = arrival.Day;
            simulation.plannedAdmissionKinds[arrival.EntityId] = arrival.Kind;
            var definition = CaseDefinitions.SingleOrDefault(definition => definition.CaseId == arrival.CaseId)
                ?? throw new ArgumentException($"Unknown case: {arrival.CaseId}", nameof(arrivals));
            simulation.patients.Add(new PatientState(
                arrival.EntityId,
                $"患者{arrival.EntityId:000}",
                arrival.ArrivalTick,
                definition.CaseId,
                definition.RequiresLab,
                definition.RequiresTreatment,
                arrival.RiskBasisPoints ?? definition.BaseRiskBasisPoints,
                definition.DiagnosisConfidenceBasisPoints,
                definition.RevenueMinorUnits)
            {
                PatienceBasisPoints = arrival.PatienceBasisPoints ?? simulation.config.PatientPatienceBasisPoints,
            });
        }

        return simulation;
    }
}
