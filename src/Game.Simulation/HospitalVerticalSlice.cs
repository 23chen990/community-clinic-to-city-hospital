namespace Game.Simulation;

public enum VerticalPatientStage
{
    NotArrived,
    Reception,
    Triage,
    Consultation,
    LabSampling,
    LabAnalysis,
    ResultPending,
    FollowUp,
    Treatment,
    Pharmacy,
    Discharged
}

public sealed record VerticalPatientView(
    string PatientId,
    VerticalPatientStage Stage,
    int WaitTicks,
    int Patience,
    string RiskStatus);

public sealed record VerticalEvent(int Tick, string PatientId, string Event, string Status);

public sealed record VerticalShiftSummary(
    int ArrivedPatients,
    int DischargedPatients,
    int PublishedResults,
    int TemporarySupportUsed,
    int OutsourcedSamples,
    int ResultBacklogWaitTicks,
    string Outcome);

public sealed record VerticalSliceSnapshot(
    int Tick,
    int ArrivedPatients,
    IReadOnlyList<VerticalPatientView> Patients,
    int SamplingQueue,
    int AnalysisQueue,
    int ResultBacklog,
    int StaffFatigue,
    bool TemporarySupportActive,
    bool OutsourcingActive,
    string CurrentBottleneck,
    string RootCause,
    bool ShiftComplete,
    VerticalShiftSummary Summary)
{
    public int PatientsInHospital => Patients.Count(patient => patient.Stage != VerticalPatientStage.NotArrived && patient.Stage != VerticalPatientStage.Discharged);
}

public sealed class HospitalVerticalSlice
{
    private readonly List<PatientState> patients =
    [
        new("患者001", "中风险"),
        new("患者002", "低风险"),
        new("患者003", "低风险")
    ];

    private readonly List<VerticalEvent> events = [];
    private int tick;
    private int staffFatigue;
    private bool temporarySupportActive;
    private bool outsourcingActive;
    private int outsourceReturnTick;
    private bool outsourcedSampleUsed;
    private bool resultPublished;
    private int resultBacklogWaitTicks;
    private bool rootCauseRecorded;

    public VerticalSliceSnapshot Snapshot
    {
        get
        {
            var patientViews = patients
                .Select(patient => new VerticalPatientView(
                    patient.PatientId,
                    patient.Stage,
                    patient.WaitTicks,
                    patient.Patience,
                    patient.RiskStatus))
                .ToArray();
            var arrivedPatients = patients.Count(patient => patient.Stage != VerticalPatientStage.NotArrived);
            var dischargedPatients = patients.Count(patient => patient.Stage == VerticalPatientStage.Discharged);
            var resultBacklog = patients.Count(patient => patient.Stage == VerticalPatientStage.ResultPending);
            var shiftComplete = dischargedPatients == patients.Count;

            return new VerticalSliceSnapshot(
                tick,
                arrivedPatients,
                patientViews,
                patients.Count(patient => patient.Stage == VerticalPatientStage.LabSampling),
                patients.Count(patient => patient.Stage == VerticalPatientStage.LabAnalysis),
                resultBacklog,
                staffFatigue,
                temporarySupportActive,
                outsourcingActive && resultBacklog > 0,
                outsourcingActive && resultBacklog > 0
                    ? "外送处理中"
                    : resultBacklog > 0
                        ? "结果发布"
                        : "无",
                resultBacklog > 0 ? "结果发布积压 → 复诊等待" : "无",
                shiftComplete,
                new VerticalShiftSummary(
                    arrivedPatients,
                    dischargedPatients,
                    resultPublished ? 1 : 0,
                    temporarySupportActive ? 1 : 0,
                    outsourcedSampleUsed ? 1 : 0,
                    resultBacklogWaitTicks,
                    shiftComplete ? "本班患者已完成诊疗并离院" : "班次进行中"));
        }
    }

    public IReadOnlyList<VerticalEvent> Events => events.ToArray();

    public void AdvanceTime(int ticks = 1)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        for (var index = 0; index < ticks; index++)
        {
            AdvanceOneTick();
        }
    }

    public bool StartTemporarySupport()
    {
        if (resultPublished || temporarySupportActive || outsourcingActive || !patients.Any(patient => patient.Stage == VerticalPatientStage.ResultPending))
        {
            return false;
        }

        temporarySupportActive = true;
        AddEvent("医院", "临时支援开始", "结果发布能力增加");
        return true;
    }

    public bool StartOutsourcedSample()
    {
        if (resultPublished || temporarySupportActive || outsourcingActive || !patients.Any(patient => patient.Stage == VerticalPatientStage.ResultPending))
        {
            return false;
        }

        outsourcingActive = true;
        outsourcedSampleUsed = true;
        outsourceReturnTick = tick + 2;
        AddEvent("医院", "外送样本开始", "普通样本交给外部处理");
        return true;
    }

    private void AdvanceOneTick()
    {
        tick++;
        staffFatigue = Math.Min(100, staffFatigue + 5);
        var publishedThisTick = TryPublishPendingResult();

        switch (tick)
        {
            case 1:
                Transition("患者001", VerticalPatientStage.Reception, "进入医院", "登记开始");
                break;
            case 2:
                Transition("患者001", VerticalPatientStage.Triage, "登记完成", "进入分诊");
                Transition("患者002", VerticalPatientStage.Reception, "进入医院", "登记开始");
                break;
            case 3:
                Transition("患者001", VerticalPatientStage.Consultation, "分诊完成", "接诊开始");
                AddEvent("患者001", "接诊开始", "医生初诊");
                Transition("患者002", VerticalPatientStage.Triage, "登记完成", "进入分诊");
                Transition("患者003", VerticalPatientStage.Reception, "进入医院", "登记开始");
                break;
            case 4:
                Transition("患者001", VerticalPatientStage.LabSampling, "诊断信心不足", "采样开始");
                AddEvent("患者001", "采样开始", "等待基础化验");
                Transition("患者002", VerticalPatientStage.Consultation, "分诊完成", "接诊开始");
                AddEvent("患者002", "接诊开始", "医生初诊");
                Transition("患者003", VerticalPatientStage.Triage, "登记完成", "进入分诊");
                break;
            case 5:
                Transition("患者001", VerticalPatientStage.LabAnalysis, "采样完成", "分析开始");
                AddEvent("患者001", "分析开始", "基础化验分析");
                Transition("患者002", VerticalPatientStage.Treatment, "接诊完成", "治疗开始");
                AddEvent("患者002", "治疗开始", "进入治疗室");
                Transition("患者003", VerticalPatientStage.Consultation, "分诊完成", "接诊开始");
                AddEvent("患者003", "接诊开始", "医生初诊");
                break;
            case 6:
                Transition("患者001", VerticalPatientStage.ResultPending, "分析完成", "结果待发布");
                AddEvent("患者001", "结果待发布", "等待结果审核");
                Transition("患者002", VerticalPatientStage.Pharmacy, "治疗完成", "药房开始");
                AddEvent("患者002", "药房开始", "等待处方交付");
                Transition("患者003", VerticalPatientStage.Treatment, "接诊完成", "治疗开始");
                AddEvent("患者003", "治疗开始", "进入治疗室");
                break;
            case 7:
                if (!resultPublished)
                {
                    RegisterResultBacklogWait();
                }

                Transition("患者002", VerticalPatientStage.Discharged, "药房完成", "患者离开");
                AddEvent("患者002", "患者离开", "已完成本次诊疗");
                Transition("患者003", VerticalPatientStage.Pharmacy, "治疗完成", "药房开始");
                AddEvent("患者003", "药房开始", "等待处方交付");
                break;
            case 8:
                if (resultPublished && !publishedThisTick)
                {
                    AdvancePublishedResultJourney();
                }
                else if (!resultPublished)
                {
                    RegisterResultBacklogWait();
                }

                Transition("患者003", VerticalPatientStage.Discharged, "药房完成", "患者离开");
                AddEvent("患者003", "患者离开", "已完成本次诊疗");
                break;
            case 9:
                if (resultPublished && !publishedThisTick)
                {
                    AdvancePublishedResultJourney();
                }
                else if (!resultPublished)
                {
                    RegisterResultBacklogWait();
                }

                break;
            case 10:
                if (resultPublished && !publishedThisTick)
                {
                    AdvancePublishedResultJourney();
                }
                else if (!resultPublished)
                {
                    RegisterResultBacklogWait();
                }

                break;
            default:
                if (!resultPublished && patients.Any(patient => patient.Stage == VerticalPatientStage.ResultPending))
                {
                    RegisterResultBacklogWait();
                }
                else if (resultPublished && !publishedThisTick)
                {
                    AdvancePublishedResultJourney();
                }

                break;
        }

        foreach (var patient in patients.Where(patient => patient.Stage == VerticalPatientStage.ResultPending))
        {
            patient.WaitTicks++;
        }
    }

    private bool TryPublishPendingResult()
    {
        if (resultPublished || !patients.Any(patient => patient.Stage == VerticalPatientStage.ResultPending))
        {
            return false;
        }

        if (temporarySupportActive)
        {
            PublishResult("结果发布");
            return true;
        }

        if (outsourcingActive && tick >= outsourceReturnTick)
        {
            PublishResult("外送结果返回");
            return true;
        }

        return false;
    }

    private void AdvancePublishedResultJourney()
    {
        var patient = patients.Single(patient => patient.PatientId == "患者001");

        switch (patient.Stage)
        {
            case VerticalPatientStage.FollowUp:
                Transition("患者001", VerticalPatientStage.Treatment, "复诊开始", "治疗开始");
                break;
            case VerticalPatientStage.Treatment:
                Transition("患者001", VerticalPatientStage.Pharmacy, "治疗完成", "药房开始");
                AddEvent("患者001", "药房开始", "等待处方交付");
                break;
            case VerticalPatientStage.Pharmacy:
                Transition("患者001", VerticalPatientStage.Discharged, "药房完成", "患者离开");
                AddEvent("患者001", "患者离开", "已完成本次诊疗");
                break;
        }
    }

    private void RegisterResultBacklogWait()
    {
        var patient = patients.Single(patient => patient.PatientId == "患者001");
        patient.Patience = Math.Max(0, patient.Patience - 10);
        patient.RiskStatus = "风险上升";
        resultBacklogWaitTicks++;

        if (!rootCauseRecorded)
        {
            AddEvent("患者001", "结果发布积压", "复诊等待增加");
            rootCauseRecorded = true;
        }
    }

    private void PublishResult(string eventName)
    {
        resultPublished = true;
        Transition("患者001", VerticalPatientStage.FollowUp, eventName, "结果返回门诊");
        AddEvent("患者001", "结果返回门诊", "进入结果复诊");
    }

    private void Transition(string patientId, VerticalPatientStage stage, string eventName, string status)
    {
        var patient = patients.Single(patient => patient.PatientId == patientId);
        if (patient.Stage == VerticalPatientStage.Discharged || patient.Stage == stage)
        {
            return;
        }

        patient.Stage = stage;
        patient.WaitTicks = 0;
        AddEvent(patientId, eventName, status);
    }

    private void AddEvent(string patientId, string eventName, string status)
    {
        events.Add(new VerticalEvent(tick, patientId, eventName, status));
    }

    private sealed class PatientState
    {
        public PatientState(string patientId, string riskStatus)
        {
            PatientId = patientId;
            RiskStatus = riskStatus;
        }

        public string PatientId { get; }
        public VerticalPatientStage Stage { get; set; } = VerticalPatientStage.NotArrived;
        public int WaitTicks { get; set; }
        public int Patience { get; set; } = 100;
        public string RiskStatus { get; set; }
    }
}
