namespace Game.Simulation;

/// <summary>
/// Deterministic M1 playtest inputs. The dispatch comparison is deliberately
/// separate from the complete white-shift rhythm so tuning one does not make
/// the other a hidden second product baseline.
/// </summary>
public static class M1PlayScenarios
{
    public const int Seed = 20_261_008;
    public const int DispatchBacklogThreshold = 2;
    public const int MainPressureWindowStartTick = 1_800;
    public const int MainPressureWindowEndTick = 2_700;
    public const int SupportBaselinePublishIntervalTicks = 200;

    // A short, reversible fixture for comparing dispatch choices at one
    // known backlog. The product owner authorized these experiment inputs on
    // 2026-10-08; they are not a second player-facing content baseline.
    public static IReadOnlyList<PatientArrival> DispatchComparisonArrivals { get; } =
        Array.AsReadOnly(
        [
            new PatientArrival(1, 1, "URTI", 1_500),
            new PatientArrival(2, 40, "HIGH_RISK_ABDOMINAL", 7_800),
            new PatientArrival(3, 100, "DEHYDRATION", 1_200),
            new PatientArrival(4, 180, "ALLERGY", 1_500),
            new PatientArrival(5, 260, "THROAT_INFECTION", 2_400),
            new PatientArrival(6, 340, "URTI", 1_200),
        ]);

    public static M1SimulationConfig DispatchComparisonConfig { get; } = new()
    {
        ShiftLengthTicks = 3_600,
        ClosingDurationTicks = 600,
        ServiceTicks = 20,
        TreatmentServiceTicks = 20,
        LabAnalysisServiceTicks = 240,
        NormalPublishIntervalTicks = 900,
        RiskPriorityPublishIntervalTicks = 900,
        TemporarySupportDurationTicks = 600,
        // 60 seconds at 10 Hz, the current M1 support baseline.
        TemporarySupportPublishIntervalTicks = SupportBaselinePublishIntervalTicks,
        OutsourceReturnTicks = 1_200,
        OutsourceCostPerSampleMinorUnits = 35,
        MaxOutsourceSamples = 2,
        InitialCashMinorUnits = 1_000,
        PatientPatienceBasisPoints = 10_000,
        PatienceLossPerWaitingTick = 1,
        RiskIncreasePerWaitingTick = 1,
        CriticalTransferWaitTicks = 5_000,
    };

    public static HospitalSimulation CreateDispatchComparison(
        int seed = Seed,
        M1SimulationConfig? config = null) =>
        HospitalSimulation.CreateScenario(
            seed,
            DispatchComparisonArrivals,
            config ?? DispatchComparisonConfig);

    // The complete white-shift fixture has one quiet, no-lab arrival first;
    // the five mixed lab cases arrive from 720 to 1800, leaving the middle of
    // the shift for the player to see the result-publication pressure build.
    // IDs 7–8 are next-day arrivals so the same plan can exercise new demand
    // alongside a retained unfinished visit after ArrangeNextDay.
    public static IReadOnlyList<PatientArrival> FirstWhiteShiftDayOneArrivals { get; } =
        Array.AsReadOnly(
        [
            new PatientArrival(1, 120, "DEHYDRATION", 1_200),
            new PatientArrival(2, 720, "URTI", 1_500),
            new PatientArrival(3, 800, "FLU_LIKE", 2_400),
            new PatientArrival(4, 900, "ALLERGY", 1_800),
            new PatientArrival(5, 1_000, "THROAT_INFECTION", 2_600),
            new PatientArrival(6, 1_800, "HIGH_RISK_ABDOMINAL", 7_000),
        ]);

    public static IReadOnlyList<PatientArrival> FirstWhiteShiftArrivals { get; } =
        Array.AsReadOnly(
            FirstWhiteShiftDayOneArrivals
                .Concat(
                [
                    new PatientArrival(7, 720, "DEHYDRATION", 1_400, Day: 2),
                    new PatientArrival(8, 1_440, "URTI", 2_000, Day: 2),
                ])
                .ToArray());

    public static M1SimulationConfig FirstWhiteShiftConfig { get; } =
        DispatchComparisonConfig with
        {
            // Keep the same operational capacities as the comparison fixture;
            // only the arrival rhythm and day-two content differ. A slower
            // publication cadence leaves an ordinary case unfinished at the
            // hard closing limit, making next-day continuity observable.
            NormalPublishIntervalTicks = 1_200,
            TemporarySupportPublishIntervalTicks = SupportBaselinePublishIntervalTicks,
        };

    public static HospitalSimulation CreateFirstWhiteShift(
        int seed = Seed,
        M1SimulationConfig? config = null) =>
        HospitalSimulation.CreateScenario(
            seed,
            FirstWhiteShiftArrivals,
            config ?? FirstWhiteShiftConfig);
}
