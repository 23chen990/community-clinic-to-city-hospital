using System.Text;

namespace Game.Simulation;

public enum ShiftPhase { Operating, Closing, Settled }

public sealed record CompletedShift(int Day, int CompletedTick, ShiftSummary Summary);

/// <summary>Reversible experiment authorized 2026-10-08, not a promise of a return.</summary>
public sealed class PatientReturnPolicy(M1SimulationConfig config)
{
    public int ChanceBasisPoints(int patience, int totalWaitTicks, bool hasCompletedAnalysis)
    {
        var chance = (long)config.ReturnBaseChanceBasisPoints
            + (long)Math.Clamp(patience, 0, 10_000) * config.ReturnPatienceWeightBasisPoints / 10_000
            + (hasCompletedAnalysis ? config.ReturnEvidenceBonusBasisPoints : 0)
            - (long)Math.Max(0, totalWaitTicks) * config.ReturnWaitPenaltyPerTickBasisPoints;
        return (int)Math.Clamp(chance, config.MinReturnChanceBasisPoints, config.MaxReturnChanceBasisPoints);
    }
}

public sealed partial class HospitalSimulation
{
    private readonly DeterministicRng returnRandom;
    private readonly Dictionary<long, int> plannedAdmissionDays = [];
    private readonly Dictionary<long, PatientArrivalKind> plannedAdmissionKinds = [];
    private readonly Dictionary<long, int> resumedServiceTicks = [];
    private readonly Dictionary<long, ReturnVisit> returnVisits = [];
    private readonly List<CompletedShift> completedShifts = [];
    private int shiftStartTick;
    private int dayEventStartIndex;
    private int dayLedgerStartIndex;
    private bool closingExtended;

    public int Day { get; private set; } = 1;
    public int ShiftTick => Tick - shiftStartTick;
    public ShiftPhase Phase { get; private set; } = ShiftPhase.Operating;
    public IReadOnlyList<CompletedShift> CompletedShifts => completedShifts.ToArray();
    private int ClosingDeadline => config.ShiftLengthTicks + config.ClosingDurationTicks
        + (closingExtended ? config.ClosingExtensionTicks : 0);
    private bool ClosingLimitReached => Phase == ShiftPhase.Closing && ShiftTick >= ClosingDeadline;

    private void BeginClosingIfDue()
    {
        if (Phase == ShiftPhase.Operating && ShiftTick >= config.ShiftLengthTicks)
        {
            Phase = ShiftPhase.Closing;
            AddEvent(SimulationEventType.ShiftClosingStarted, "医院", "白班结束，处理在院患者");
        }
    }

    private bool CanArrive(PatientState patient)
    {
        if (Phase != ShiftPhase.Operating ||
            plannedAdmissionDays.GetValueOrDefault(patient.NumericId, 1) != Day ||
            patient.ArrivalTick > ShiftTick)
        {
            return false;
        }

        // CURRENT §5.4: each arrival class has its own cutoff.
        return plannedAdmissionKinds.GetValueOrDefault(patient.NumericId, PatientArrivalKind.Ordinary) switch
        {
            PatientArrivalKind.Story => ShiftTick < 3_300,
            PatientArrivalKind.Urgent => true,
            _ => ShiftTick < 3_000 || RiskLevelFor(patient.RiskBasisPoints) > RiskLevel.Low,
        };
    }

    private CommandResult ExtendClosing(SimulationCommand command)
    {
        if (Phase != ShiftPhase.Closing || closingExtended ||
            !patients.Any(patient => IsInHospital(patient) && RiskLevelFor(patient.RiskBasisPoints) >= RiskLevel.High))
        {
            return RejectedInvalid(command, "仅在收尾仍有高风险患者时可延长一次");
        }

        closingExtended = true;
        AddEvent(SimulationEventType.ShiftExtended, "医院", "已延长一次收尾，不接收新患者");
        return Accepted(command, "延长收尾已生效");
    }

    private CommandResult ArrangeNextDay(SimulationCommand command)
    {
        if (!IsShiftComplete)
        {
            return RejectedInvalid(command, "本班尚未结算");
        }

        Day++;
        shiftStartTick = Tick;
        dayEventStartIndex = events.Count;
        dayLedgerStartIndex = ledgerEntries.Count;
        closingExtended = false;
        Phase = ShiftPhase.Operating;
        dispatchPlan = LabDispatchPlan.None;
        supportUntilTick = -1;
        nextResultPublishTick = Tick + ApplyFatigueDuration(config.NormalPublishIntervalTicks);
        paused = false;
        AddEvent(SimulationEventType.NextDayStarted, "医院", $"第{Day}日白班开始");
        return Accepted(command, "下一营业日已开始");
    }

    private void SettleShiftIfDue()
    {
        if (Phase != ShiftPhase.Closing)
        {
            return;
        }

        if (!ClosingLimitReached && patients.Any(IsInHospital))
        {
            return;
        }

        foreach (var patient in patients.Where(IsInHospital).OrderBy(patient => patient.NumericId).ToArray())
        {
            FinishUnresolvedVisit(patient);
        }

        if (dispatchPlan == LabDispatchPlan.TemporarySupport)
        {
            AddEvent(SimulationEventType.TemporarySupportEnded, "医院", "收尾结束，临时支援结束");
        }
        dispatchPlan = LabDispatchPlan.None;
        supportUntilTick = -1;
        Phase = ShiftPhase.Settled;
        AddEvent(SimulationEventType.ShiftSettled, "医院", "白班结算");
        completedShifts.Add(new CompletedShift(Day, Tick, CreateShiftSummary()));
    }

    private void FinishUnresolvedVisit(PatientState patient)
    {
        var previousStage = patient.Stage;
        var resumeStage = ResumeStageFor(previousStage);
        var remainingServiceTicks = 0;
        foreach (var task in activeTasks.Values.Where(task => task.Patient == patient).ToArray())
        {
            remainingServiceTicks = Math.Max(1, task.EndTick - Tick);
            activeTasks.Remove(task.Service);
            resources.Release(task.Resources);
        }
        CloseOpenRootCause(patient);
        RemoveFromQueues(patient);
        var highRisk = RiskLevelFor(patient.RiskBasisPoints) >= RiskLevel.High;
        patient.Stage = highRisk ? PatientStage.Transferred : PatientStage.LeftUnfinished;
        AddEvent(highRisk ? SimulationEventType.PatientTransferred : SimulationEventType.PatientLeftUnfinished,
            patient.PatientId,
            highRisk ? "收尾仍有高风险，安排安全转院" : "本次诊疗未完成，患者离院");
        rootCauseEdges.Add(new RootCauseEdge(RootCauseSourceFor(previousStage), patient.PatientId,
            RootCauseCode.ShiftClosingLimit, patient.StageEnteredTick, Tick, patient.WaitTicks));

        if (highRisk)
        {
            return;
        }

        var hasAnalysis = patient.LabEvidenceApplied || previousStage == PatientStage.ResultPending;
        var chance = new PatientReturnPolicy(config).ChanceBasisPoints(
            patient.PatienceBasisPoints, patient.TotalWaitingTicks, hasAnalysis);
        if (returnRandom.Next(0, 10_000) < chance)
        {
            returnVisits[patient.NumericId] = new ReturnVisit(Day + 1, config.ReturnArrivalDelayTicks, resumeStage, remainingServiceTicks);
        }
        // No patient revenue: an unfinished visit is not a completed course of care.
    }

    private void ProcessReturningPatients()
    {
        if (Phase != ShiftPhase.Operating)
        {
            return;
        }

        foreach (var pair in returnVisits.Where(pair => pair.Value.Day == Day && pair.Value.ArrivalTick <= ShiftTick)
                     .OrderBy(pair => pair.Key).ToArray())
        {
            var patient = patients.Single(patient => patient.NumericId == pair.Key);
            returnVisits.Remove(pair.Key);
            patient.Stage = pair.Value.ResumeStage;
            patient.StageEnteredTick = Tick;
            patient.WaitTicks = 0;
            patient.PatienceBasisPoints = config.ReturningPatienceBasisPoints;
            if (pair.Value.RemainingServiceTicks > 0)
            {
                resumedServiceTicks[patient.NumericId] = pair.Value.RemainingServiceTicks;
            }
            var service = WaitingServiceFor(patient.Stage);
            if (service.HasValue)
            {
                Enqueue(service.Value, patient);
            }
            else if (patient.Stage == PatientStage.ResultPending)
            {
                resultQueue.Add(patient);
                rootCauseEdges.Add(new RootCauseEdge("基础化验.结果发布", patient.PatientId,
                    RootCauseCode.ResultPublicationBacklog, Tick, -1, 1));
            }
            else if (patient.Stage == PatientStage.Outsourced)
            {
                outsourcedQueue.Add(patient);
            }
            AddEvent(SimulationEventType.PatientReturnedNextDay, patient.PatientId, "患者再次到院，延续上次诊疗进度");
            rootCauseEdges.Add(new RootCauseEdge("上次未完成诊疗", patient.PatientId,
                RootCauseCode.UnfinishedVisitReturn, Tick, Tick, 1));
        }
    }

    private ShiftSummary CreateShiftSummary()
    {
        var dayEvents = events.Skip(dayEventStartIndex).ToArray();
        int Count(SimulationEventType type) => dayEvents.Count(entry => entry.EventType == type);
        var todayLedger = ledgerEntries.Skip(dayLedgerStartIndex).ToArray();
        var unfinished = Count(SimulationEventType.PatientLeftUnfinished);
        return new ShiftSummary(
            Count(SimulationEventType.PatientEntered) + Count(SimulationEventType.PatientReturnedNextDay),
            Count(SimulationEventType.PatientDischarged),
            Count(SimulationEventType.PatientTransferred),
            patients.Count(patient => patient.Stage is PatientStage.WaitingTreatment or PatientStage.InTreatment or
                PatientStage.WaitingPharmacy or PatientStage.InPharmacy),
            Count(SimulationEventType.ResultPublished),
            todayLedger.Where(entry => entry.Kind == LedgerKind.PatientRevenue).Sum(entry => entry.AmountMinorUnits),
            IsShiftComplete ? $"白班结束，未完成诊疗离院 {unfinished} 人" : "班次进行中",
            DepartmentStatuses())
        {
            UnfinishedPatients = unfinished,
            ReturningPatients = Count(SimulationEventType.PatientReturnedNextDay),
            LeftWithoutCarePatients = Count(SimulationEventType.PatientLeftWithoutCare),
            DispatchCostMinorUnits = todayLedger.Where(entry => entry.Kind == LedgerKind.DispatchCost).Sum(entry => entry.AmountMinorUnits),
        };
    }

    private void AppendShiftCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "shift");
        AppendInt(builder, Day);
        AppendInt(builder, shiftStartTick);
        AppendEnum(builder, Phase);
        AppendBool(builder, closingExtended);
        AppendLong(builder, returnRandom.State);
        AppendInt(builder, dayLedgerStartIndex);
        // Presentation-only speed events can shift the physical event index.
        AppendInt(builder, events.Take(dayEventStartIndex).Count(entry => entry.EventType != SimulationEventType.SpeedChanged));
        AppendCount(builder, plannedAdmissionDays.Count);
        foreach (var admission in plannedAdmissionDays.OrderBy(pair => pair.Key))
        {
            AppendLong(builder, admission.Key);
            AppendInt(builder, admission.Value);
            AppendEnum(builder, plannedAdmissionKinds.GetValueOrDefault(admission.Key, PatientArrivalKind.Ordinary));
        }
        AppendCount(builder, returnVisits.Count);
        foreach (var visit in returnVisits.OrderBy(pair => pair.Key))
        {
            AppendLong(builder, visit.Key);
            AppendInt(builder, visit.Value.Day);
            AppendInt(builder, visit.Value.ArrivalTick);
            AppendEnum(builder, visit.Value.ResumeStage);
            AppendInt(builder, visit.Value.RemainingServiceTicks);
        }
        AppendCount(builder, resumedServiceTicks.Count);
        foreach (var remaining in resumedServiceTicks.OrderBy(pair => pair.Key))
        {
            AppendLong(builder, remaining.Key);
            AppendInt(builder, remaining.Value);
        }
    }

    private int ResumeDurationOrDefault(PatientState patient, int duration) =>
        resumedServiceTicks.Remove(patient.NumericId, out var remaining) ? remaining : duration;

    private static bool IsInHospital(PatientState patient) => patient.Stage != PatientStage.Spawned && !IsTerminal(patient);

    private static PatientStage ResumeStageFor(PatientStage stage) => stage switch
    {
        PatientStage.InReception => PatientStage.WaitingReception,
        PatientStage.InTriage => PatientStage.WaitingTriage,
        PatientStage.InConsult => PatientStage.WaitingConsult,
        PatientStage.InLabSampling => PatientStage.WaitingLabSampling,
        PatientStage.InLabAnalysis => PatientStage.WaitingLabAnalysis,
        PatientStage.InFollowUp => PatientStage.WaitingFollowUp,
        PatientStage.InTreatment => PatientStage.WaitingTreatment,
        PatientStage.InPharmacy => PatientStage.WaitingPharmacy,
        _ => stage,
    };

    private static ServiceKind? WaitingServiceFor(PatientStage stage) => stage switch
    {
        PatientStage.WaitingReception => ServiceKind.Reception,
        PatientStage.WaitingTriage => ServiceKind.Triage,
        PatientStage.WaitingConsult => ServiceKind.InitialConsultation,
        PatientStage.WaitingLabSampling => ServiceKind.LabSampling,
        PatientStage.WaitingLabAnalysis => ServiceKind.LabAnalysis,
        PatientStage.WaitingFollowUp => ServiceKind.FollowUp,
        PatientStage.WaitingTreatment => ServiceKind.Treatment,
        PatientStage.WaitingPharmacy => ServiceKind.Pharmacy,
        _ => null,
    };

    private sealed record ReturnVisit(int Day, int ArrivalTick, PatientStage ResumeStage, int RemainingServiceTicks);
}
