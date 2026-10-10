using System.Security.Cryptography;
using System.Text;

namespace Game.Simulation;

public enum PatientStage
{
    Spawned,
    WaitingReception,
    InReception,
    WaitingTriage,
    InTriage,
    WaitingConsult,
    InConsult,
    WaitingLabSampling,
    InLabSampling,
    WaitingLabAnalysis,
    InLabAnalysis,
    ResultPending,
    Outsourced,
    WaitingFollowUp,
    InFollowUp,
    WaitingTreatment,
    InTreatment,
    WaitingPharmacy,
    InPharmacy,
    Completed,
    Transferred,
    LeftWithoutCare,
    LeftUnfinished,
}

public enum RiskLevel
{
    Routine,
    Low,
    Medium,
    High,
    Critical,
}

public enum LabDispatchPlan
{
    None,
    RiskPriority,
    TemporarySupport,
    OutsourceSamples,
}

public enum SimulationSpeed
{
    Half,
    Normal,
    Double,
}

public enum SimulationCommandType
{
    DispatchLab,
    Pause,
    Resume,
    SetSpeed,
    ArrangeNextDay,
    ExtendClosing,
}

public enum CommandResultStatus
{
    Accepted,
    Deferred,
    RejectedInvalid,
    RejectedStale,
    RejectedDuplicate,
    RejectedInsufficient,
}

public enum SimulationEventType
{
    PatientEntered,
    RegistrationStarted,
    RegistrationCompleted,
    TriageStarted,
    TriageCompleted,
    ConsultationStarted,
    ConsultationCompleted,
    LabOrdered,
    LabSamplingStarted,
    LabSamplingCompleted,
    LabAnalysisStarted,
    LabAnalysisCompleted,
    ResultPending,
    ResultPublished,
    ResultReturnedToConsultation,
    FollowUpStarted,
    FollowUpCompleted,
    TreatmentStarted,
    TreatmentCompleted,
    PharmacyStarted,
    PharmacyCompleted,
    PatientDischarged,
    PatientTransferred,
    PatientLeftWithoutCare,
    TemporarySupportStarted,
    TemporarySupportEnded,
    RiskPriorityStarted,
    OutsourcedSampleStarted,
    OutsourcedResultReturned,
    CommandRejected,
    SpeedChanged,
    DiagnosisEvidenceApplied,
    ShiftClosingStarted,
    ShiftExtended,
    ShiftSettled,
    NextDayStarted,
    PatientLeftUnfinished,
    PatientReturnedNextDay,
}

public enum RootCauseCode
{
    ResultPublicationBacklog,
    ResultReturnedToFollowUp,
    PatienceExpired,
    CriticalWaitSafetyTransfer,
    ResourceUnavailable,
    ShiftClosingLimit,
    UnfinishedVisitReturn,
}

public enum LedgerKind
{
    PatientRevenue,
    DispatchCost,
    Refund,
}

public sealed partial record M1SimulationConfig
{
    public int ShiftLengthTicks { get; init; } = 3_600;
    public int TemporarySupportDurationTicks { get; init; } = 600;
    public int TemporarySupportFatigueBasisPoints { get; init; } = 1_500;
    public int TemporarySupportPublishIntervalTicks { get; init; } = 200;
    public int RiskPriorityPublishIntervalTicks { get; init; } = 100;
    public int NormalPublishIntervalTicks { get; init; } = 900;
    // Experimental M1 tuning authorized 2026-10-08; CURRENT §9.5.
    public int OutsourceReturnTicks { get; init; } = 1_200;
    public long OutsourceCostPerSampleMinorUnits { get; init; } = 40;
    public int MaxOutsourceSamples { get; init; } = 2;
    public long InitialCashMinorUnits { get; init; } = 1_000;
    public int FatigueDurationPenaltyBasisPoints { get; init; } = 10_000;
    public int LabAnalysisServiceTicks { get; init; } = 100;
    public int ServiceTicks { get; init; } = 1;
    public int TreatmentServiceTicks { get; init; } = 2;
    public int PatientPatienceBasisPoints { get; init; } = 10_000;
    public int PatienceLossPerWaitingTick { get; init; } = 1;
    public int RiskIncreasePerWaitingTick { get; init; } = 1;
    public int DiagnosisConfidenceThreshold { get; init; } = 7_000;
    public int CriticalTransferWaitTicks { get; init; } = 1_200;
    public int ClosingDurationTicks { get; init; } = 600;
    public int ClosingExtensionTicks { get; init; } = 300;
    // Experimental retention tuning, authorized 2026-10-08. No overnight shift.
    public int ReturnBaseChanceBasisPoints { get; init; } = 4_000;
    public int ReturnPatienceWeightBasisPoints { get; init; } = 3_000;
    public int ReturnEvidenceBonusBasisPoints { get; init; } = 2_000;
    public int ReturnWaitPenaltyPerTickBasisPoints { get; init; } = 1;
    public int MinReturnChanceBasisPoints { get; init; } = 1_000;
    public int MaxReturnChanceBasisPoints { get; init; } = 9_500;
    public int ReturningPatienceBasisPoints { get; init; } = 8_000;
    public int ReturnArrivalDelayTicks { get; init; } = 20;
}

public sealed record SimulationCommand(
    long CommandId,
    int SubmittedTick,
    SimulationCommandType Type,
    LabDispatchPlan DispatchPlan = LabDispatchPlan.None,
    SimulationSpeed Speed = SimulationSpeed.Normal)
{
    public static SimulationCommand DispatchLab(long commandId, int tick, LabDispatchPlan plan) =>
        new(commandId, tick, SimulationCommandType.DispatchLab, plan);

    public static SimulationCommand Pause(long commandId, int tick) =>
        new(commandId, tick, SimulationCommandType.Pause);

    public static SimulationCommand Resume(long commandId, int tick) =>
        new(commandId, tick, SimulationCommandType.Resume);

    public static SimulationCommand SetSpeed(long commandId, int tick, SimulationSpeed speed) =>
        new(commandId, tick, SimulationCommandType.SetSpeed, Speed: speed);
}

public sealed record CommandResult(CommandResultStatus Status, long CommandId, string Reason);

public sealed record SimulationEvent(
    int Tick,
    SimulationEventType EventType,
    string PatientId,
    string Status,
    string? Detail = null);

public sealed record PatientSnapshot(
    string PatientId,
    string CaseId,
    PatientStage Stage,
    int WaitTicks,
    int PatienceBasisPoints,
    int RiskBasisPoints,
    RiskLevel RiskLevel,
    int DiagnosisConfidenceBasisPoints,
    bool RequiresLab,
    bool RequiresTreatment);

public sealed record LabSnapshot(
    int SamplingQueue,
    int AnalysisQueue,
    int ResultBacklog,
    int OutsourcedSamples,
    int TechnicianFatigueBasisPoints,
    bool TemporarySupportActive,
    LabDispatchPlan DispatchPlan,
    string CurrentBottleneck);

public sealed record ShiftSummary(
    int ArrivedPatients,
    int DischargedPatients,
    int TransferredPatients,
    int TreatmentInProgress,
    int PublishedResults,
    long RevenueMinorUnits,
    string Outcome,
    IReadOnlyDictionary<string, string> DepartmentStatuses)
{
    public int UnfinishedPatients { get; init; }
    public int ReturningPatients { get; init; }
    public int LeftWithoutCarePatients { get; init; }
    public long DispatchCostMinorUnits { get; init; }
}

public sealed record RootCauseEdge(
    string CauseEntity,
    string EffectEntity,
    RootCauseCode ReasonCode,
    int StartTick,
    int EndTick,
    int Magnitude,
    string? ParentEdgeId = null);

public sealed record LedgerEntry(
    string IdempotencyKey,
    LedgerKind Kind,
    long AmountMinorUnits,
    int Tick,
    string EntityId);

public sealed record HospitalSnapshot(
    int Tick,
    int ArrivedPatients,
    IReadOnlyList<PatientSnapshot> Patients,
    LabSnapshot Lab,
    bool Paused,
    SimulationSpeed Speed,
    bool ShiftComplete,
    int OrphanTaskCount,
    int NegativeResourceCount,
    IReadOnlyDictionary<string, int> ResourceUsage,
    ShiftSummary Summary)
{
    public int PatientsInHospital => Patients.Count(patient => patient.Stage is not
        (PatientStage.Spawned or PatientStage.Completed or PatientStage.Transferred or PatientStage.LeftWithoutCare or PatientStage.LeftUnfinished));
    public int Day { get; init; }
    public int ShiftTick { get; init; }
    public ShiftPhase Phase { get; init; }
    public long CashMinorUnits { get; init; }
}

public sealed partial class HospitalSimulation
{
    public const int TickRate = 10;
    public const int TemporarySupportDurationTicks = 600;
    public const int TemporarySupportFatigueBasisPoints = 1_500;

    private readonly M1SimulationConfig config;
    private readonly List<PatientState> patients = [];
    private readonly List<SimulationEvent> events = [];
    private readonly List<RootCauseEdge> rootCauseEdges = [];
    private readonly List<LedgerEntry> ledgerEntries = [];
    private readonly List<SimulationCommand> commandLog = [];
    private readonly List<SimulationCommand> pendingCommands = [];
    private readonly HashSet<long> seenCommandIds = [];
    private readonly Dictionary<ServiceKind, List<PatientState>> queues = [];
    private readonly Dictionary<ServiceKind, ActiveTask> activeTasks = [];
    private readonly ResourcePool resources;
    private readonly DeterministicRng random;
    private readonly List<PatientState> resultQueue = [];
    private readonly List<PatientState> outsourcedQueue = [];

    private long nextTaskId = 1;
    private int nextResultPublishTick;
    private int supportUntilTick = -1;
    private LabDispatchPlan dispatchPlan;
    private int technicianFatigueBasisPoints;
    private bool paused;
    private SimulationSpeed speed = SimulationSpeed.Normal;

    private HospitalSimulation(int seed, M1SimulationConfig? config)
    {
        this.config = config ?? new M1SimulationConfig();
        this.config.Validate();
        nextResultPublishTick = this.config.NormalPublishIntervalTicks;
        random = new DeterministicRng(seed);
        returnRandom = new DeterministicRng(seed ^ 0x51ED270B);
        resources = new ResourcePool(
            ("Receptionist", 1),
            ("TriageNurse", 1),
            ("GeneralPractitioner", 1),
            ("SamplingStation", 1),
            ("SamplingNurse", 1),
            ("LabTechnician", 1),
            ("Analyzer", 1),
            ("TreatmentNurse", 1),
            ("Pharmacist", 1));

        foreach (var service in ServiceOrder)
        {
            queues[service] = [];
        }
    }

    public static HospitalSimulation CreateBaseline(int seed, M1SimulationConfig? config = null)
    {
        var simulation = new HospitalSimulation(seed, config);
        simulation.CreateBaselinePatients();
        return simulation;
    }

    public int Tick { get; private set; }

    public bool Paused => paused;

    public SimulationSpeed Speed => speed;

    public IReadOnlyList<SimulationEvent> Events => events.ToArray();

    public IReadOnlyList<RootCauseEdge> RootCauseEdges => rootCauseEdges.ToArray();

    public IReadOnlyList<LedgerEntry> LedgerEntries => ledgerEntries.ToArray();

    public IReadOnlyList<SimulationCommand> CommandLog => commandLog.ToArray();

    public HospitalSnapshot Snapshot
    {
        get
        {
            var patientSnapshots = patients
                .OrderBy(patient => patient.NumericId)
                .Select(ToSnapshot)
                .ToArray();
            var resultBacklog = resultQueue.Count;
            var lab = new LabSnapshot(
                queues[ServiceKind.LabSampling].Count,
                queues[ServiceKind.LabAnalysis].Count,
                resultBacklog,
                outsourcedQueue.Count,
                technicianFatigueBasisPoints,
                dispatchPlan == LabDispatchPlan.TemporarySupport && Tick < supportUntilTick,
                dispatchPlan,
                resultBacklog > 0
                    ? "结果发布"
                    : queues[ServiceKind.LabAnalysis].Count > 0
                        ? "分析"
                        : queues[ServiceKind.LabSampling].Count > 0
                            ? "采样"
                            : "无");

            var summary = CreateShiftSummary();

            return new HospitalSnapshot(
                Tick,
                summary.ArrivedPatients,
                patientSnapshots,
                lab,
                paused,
                speed,
                IsShiftComplete,
                OrphanTaskCount(),
                resources.NegativeResourceCount,
                resources.Usage(),
                summary)
            {
                Day = Day,
                ShiftTick = ShiftTick,
                Phase = Phase,
                CashMinorUnits = CashMinorUnits,
            };
        }
    }

    public string StateHash => ComputeStateHash();

    public long CashMinorUnits => config.InitialCashMinorUnits + ledgerEntries.Sum(entry =>
        entry.Kind == LedgerKind.DispatchCost ? -entry.AmountMinorUnits : entry.AmountMinorUnits);

    public bool IsShiftComplete => Phase == ShiftPhase.Settled;

    public void AdvanceTicks(int ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        for (var index = 0; index < ticks; index++)
        {
            AdvanceTick();
        }
    }

    public void AdvanceTick()
    {
        ApplyCommands();
        if (paused || IsShiftComplete)
        {
            return;
        }

        Tick++;
        BeginClosingIfDue();
        UpdateWaitingPatients();
        AutoSlowForNewCriticalPatients();
        FinishTasks();
        ProcessOutsourcedResults();
        ProcessResultPublication();
        if (!ClosingLimitReached)
        {
            ScheduleServices();
        }
        ArrivePatients();
        ProcessReturningPatients();
        AutoSlowForNewCriticalPatients();
        SettleShiftIfDue();
    }

    public bool AdvanceUntil(Func<HospitalSnapshot, bool> predicate, int maxTicks)
    {
        if (maxTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTicks));
        }

        for (var index = 0; index < maxTicks; index++)
        {
            if (predicate(Snapshot))
            {
                return true;
            }

            var previousTick = Tick;
            AdvanceTick();
            if (previousTick == Tick)
            {
                return false;
            }
        }

        return predicate(Snapshot);
    }

    private IReadOnlyDictionary<string, string> DepartmentStatuses()
    {
        var statuses = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["接待与分诊"] = queues[ServiceKind.Reception].Count + queues[ServiceKind.Triage].Count > 0 ? "繁忙" : "平稳",
            ["全科门诊"] = queues[ServiceKind.InitialConsultation].Count + queues[ServiceKind.FollowUp].Count > 0 ? "繁忙" : "平稳",
            ["基础化验室"] = resultQueue.Count > 0 ? "结果发布积压" : "已缓解",
            ["治疗室"] = queues[ServiceKind.Treatment].Count > 0 ? "繁忙" : "平稳",
            ["药房"] = queues[ServiceKind.Pharmacy].Count > 0 ? "繁忙" : "平稳",
        };
        return statuses;
    }

    private int OrphanTaskCount()
    {
        var count = 0;
        foreach (var task in activeTasks.Values)
        {
            if (IsTerminal(task.Patient) || StageForStart(task.Service) != task.Patient.Stage)
            {
                count++;
            }
        }

        return count;
    }

    private void AddEvent(SimulationEventType eventType, string patientId, string status, string? detail = null)
    {
        events.Add(new SimulationEvent(Tick, eventType, patientId, status, detail));
    }

    private enum ServiceKind
    {
        Reception,
        Triage,
        InitialConsultation,
        LabSampling,
        LabAnalysis,
        FollowUp,
        Treatment,
        Pharmacy,
    }

    private sealed record ActiveTask(
        long TaskId,
        ServiceKind Service,
        PatientState Patient,
        int EndTick,
        IReadOnlyList<string> Resources);

    private sealed class DeterministicRng
    {
        private uint state;

        public DeterministicRng(int seed)
        {
            state = unchecked((uint)seed);
            if (state == 0)
            {
                state = 0x9E3779B9u;
            }
        }

        public uint State => state;

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            if (minimumInclusive >= maximumExclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumExclusive));
            }

            state = unchecked(state * 1664525u + 1013904223u);
            return minimumInclusive + (int)(state % (uint)(maximumExclusive - minimumInclusive));
        }
    }
}
