namespace Game.Simulation;

public sealed partial class HospitalSimulation
{
    private static readonly CaseDefinition[] CaseDefinitions =
        M1CaseCatalog.All
            .Select(definition => new CaseDefinition(
                definition.CaseId,
                definition.RequiresLab,
                definition.RequiresTreatment,
                definition.BaseRiskBasisPoints,
                definition.InitialDiagnosisConfidenceBasisPoints,
                definition.RevenueMinorUnits))
            .ToArray();

    private void CreateBaselinePatients()
    {
        for (var index = 0; index < 5; index++)
        {
            var requiresLab = index < 4;
            var candidates = CaseDefinitions
                .Where(definition => definition.RequiresLab == requiresLab)
                .ToArray();
            var definition = candidates[(index + random.Next(0, candidates.Length)) % candidates.Length];
            var risk = Math.Clamp(definition.BaseRiskBasisPoints + random.Next(0, 1_001), 0, 10_000);
            patients.Add(new PatientState(
                index + 1,
                $"患者{index + 1:000}",
                index + 1,
                definition.CaseId,
                definition.RequiresLab,
                definition.RequiresTreatment,
                risk,
                definition.DiagnosisConfidenceBasisPoints,
                definition.RevenueMinorUnits)
            {
                PatienceBasisPoints = config.PatientPatienceBasisPoints,
            });
        }
    }

    private void ArrivePatients()
    {
        foreach (var patient in patients
                     .Where(patient => patient.Stage == PatientStage.Spawned && CanArrive(patient))
                     .OrderBy(patient => patient.NumericId))
        {
            patient.Stage = PatientStage.WaitingReception;
            patient.StageEnteredTick = Tick;
            Enqueue(ServiceKind.Reception, patient);
            AddEvent(SimulationEventType.PatientEntered, patient.PatientId, "进入接待与分诊");
        }
    }

    private void UpdateWaitingPatients()
    {
        foreach (var patient in patients.OrderBy(patient => patient.NumericId))
        {
            if (!IsWaiting(patient.Stage))
            {
                continue;
            }

            patient.WaitTicks++;
            patient.TotalWaitingTicks++;
            patient.PatienceBasisPoints = Math.Max(0,
                patient.PatienceBasisPoints - config.PatienceLossPerWaitingTick);
            patient.RiskBasisPoints = Math.Min(10_000,
                patient.RiskBasisPoints + config.RiskIncreasePerWaitingTick);

            if (RiskLevelFor(patient.RiskBasisPoints) == RiskLevel.Critical &&
                patient.WaitTicks >= config.CriticalTransferWaitTicks)
            {
                var waitingStage = patient.Stage;
                CloseOpenRootCause(patient);
                RemoveFromQueues(patient);
                patient.Stage = PatientStage.Transferred;
                AddEvent(SimulationEventType.PatientTransferred, patient.PatientId, "危急等待超出安全预算，转院");
                rootCauseEdges.Add(new RootCauseEdge(
                    RootCauseSourceFor(waitingStage),
                    patient.PatientId,
                    RootCauseCode.CriticalWaitSafetyTransfer,
                    patient.StageEnteredTick,
                    Tick,
                    patient.WaitTicks));
                continue;
            }

            if (patient.PatienceBasisPoints == 0 && RiskLevelFor(patient.RiskBasisPoints) < RiskLevel.High)
            {
                var waitingStage = patient.Stage;
                CloseOpenRootCause(patient);
                RemoveFromQueues(patient);
                patient.Stage = PatientStage.LeftWithoutCare;
                AddEvent(SimulationEventType.PatientLeftWithoutCare, patient.PatientId, "耐心归零离院");
                rootCauseEdges.Add(new RootCauseEdge(
                    RootCauseSourceFor(waitingStage),
                    patient.PatientId,
                    RootCauseCode.PatienceExpired,
                    patient.StageEnteredTick,
                    Tick,
                    patient.WaitTicks));
            }
        }
    }

    private void FinishTasks()
    {
        foreach (var task in activeTasks.Values
                     .Where(task => task.EndTick <= Tick)
                     .OrderBy(task => task.TaskId)
                     .ToArray())
        {
            activeTasks.Remove(task.Service);
            resources.Release(task.Resources);
            CompleteService(task);
        }
    }

    private void CompleteService(ActiveTask task)
    {
        var patient = task.Patient;
        switch (task.Service)
        {
            case ServiceKind.Reception:
                patient.Stage = PatientStage.WaitingTriage;
                patient.WaitTicks = 0;
                Enqueue(ServiceKind.Triage, patient);
                AddEvent(SimulationEventType.RegistrationCompleted, patient.PatientId, "登记完成");
                break;
            case ServiceKind.Triage:
                patient.Stage = PatientStage.WaitingConsult;
                patient.WaitTicks = 0;
                Enqueue(ServiceKind.InitialConsultation, patient);
                AddEvent(SimulationEventType.TriageCompleted, patient.PatientId, "分诊完成");
                break;
            case ServiceKind.InitialConsultation:
                patient.WaitTicks = 0;
                AddEvent(SimulationEventType.ConsultationCompleted, patient.PatientId, "初诊完成");
                if (patient.DiagnosisConfidenceBasisPoints < config.DiagnosisConfidenceThreshold)
                {
                    patient.Stage = PatientStage.WaitingLabSampling;
                    Enqueue(ServiceKind.LabSampling, patient);
                    AddEvent(SimulationEventType.LabOrdered, patient.PatientId, "诊断信心不足，安排基础化验");
                }
                else
                {
                    if (patient.RequiresTreatment)
                    {
                        patient.Stage = PatientStage.WaitingTreatment;
                        Enqueue(ServiceKind.Treatment, patient);
                    }
                    else
                    {
                        CompleteWithoutTreatment(patient);
                    }
                }
                break;
            case ServiceKind.LabSampling:
                patient.Stage = PatientStage.WaitingLabAnalysis;
                patient.WaitTicks = 0;
                Enqueue(ServiceKind.LabAnalysis, patient);
                AddEvent(SimulationEventType.LabSamplingCompleted, patient.PatientId, "采样完成");
                break;
            case ServiceKind.LabAnalysis:
                patient.Stage = PatientStage.ResultPending;
                patient.StageEnteredTick = Tick;
                patient.WaitTicks = 0;
                resultQueue.Add(patient);
                rootCauseEdges.Add(new RootCauseEdge(
                    "基础化验.结果发布",
                    patient.PatientId,
                    RootCauseCode.ResultPublicationBacklog,
                    Tick,
                    -1,
                    1));
                AddEvent(SimulationEventType.LabAnalysisCompleted, patient.PatientId, "分析完成");
                AddEvent(SimulationEventType.ResultPending, patient.PatientId, "等待结果发布");
                break;
            case ServiceKind.FollowUp:
                patient.WaitTicks = 0;
                AddEvent(SimulationEventType.FollowUpCompleted, patient.PatientId, "结果复诊完成");
                if (patient.RequiresTreatment)
                {
                    patient.Stage = PatientStage.WaitingTreatment;
                    Enqueue(ServiceKind.Treatment, patient);
                }
                else
                {
                    CompleteWithoutTreatment(patient);
                }
                break;
            case ServiceKind.Treatment:
                patient.Stage = PatientStage.WaitingPharmacy;
                patient.WaitTicks = 0;
                AddEvent(SimulationEventType.TreatmentCompleted, patient.PatientId, "治疗完成");
                Enqueue(ServiceKind.Pharmacy, patient);
                break;
            case ServiceKind.Pharmacy:
                patient.Stage = PatientStage.Completed;
                patient.WaitTicks = 0;
                AddEvent(SimulationEventType.PharmacyCompleted, patient.PatientId, "药房完成");
                AddEvent(SimulationEventType.PatientDischarged, patient.PatientId, "患者离院");
                AppendRevenue(patient);
                break;
            default:
                throw new InvalidOperationException($"未处理的服务类型：{task.Service}");
        }
    }

    private void RemoveFromQueues(PatientState patient)
    {
        foreach (var queue in queues.Values)
        {
            queue.Remove(patient);
        }

        resultQueue.Remove(patient);
        outsourcedQueue.Remove(patient);
    }

    private void EnqueueRootCause(PatientState patient, RootCauseCode code)
    {
        rootCauseEdges.Add(new RootCauseEdge(
            "基础化验.结果发布",
            "全科门诊.结果复诊",
            code,
            Tick,
            Tick,
            1,
            patient.PatientId));
    }

    private void AppendRevenue(PatientState patient)
    {
        var key = $"patient:{patient.PatientId}:revenue";
        if (ledgerEntries.Any(entry => entry.IdempotencyKey == key))
        {
            return;
        }

        ledgerEntries.Add(new LedgerEntry(key, LedgerKind.PatientRevenue, patient.RevenueMinorUnits, Tick, patient.PatientId));
    }

    private void CompleteWithoutTreatment(PatientState patient)
    {
        patient.Stage = PatientStage.Completed;
        patient.WaitTicks = 0;
        AddEvent(SimulationEventType.PatientDischarged, patient.PatientId, "无需治疗，患者离院");
        AppendRevenue(patient);
    }

    private void ApplyLabEvidence(PatientState patient)
    {
        if (!patient.RequiresLab || patient.LabEvidenceApplied ||
            !M1CaseCatalog.TryGet(patient.CaseId, out var definition))
        {
            return;
        }

        var confidenceBefore = patient.DiagnosisConfidenceBasisPoints;
        patient.ApplyLabEvidence(definition.LabEvidence);
        CloseOpenRootCause(patient);
        AddEvent(
            SimulationEventType.DiagnosisEvidenceApplied,
            patient.PatientId,
            "化验证据更新诊断信心",
            $"{confidenceBefore}->{patient.DiagnosisConfidenceBasisPoints}");
    }

    private void CloseOpenRootCause(PatientState patient)
    {
        for (var index = rootCauseEdges.Count - 1; index >= 0; index--)
        {
            var edge = rootCauseEdges[index];
            if (edge.EffectEntity == patient.PatientId &&
                edge.ReasonCode == RootCauseCode.ResultPublicationBacklog &&
                edge.EndTick < 0)
            {
                rootCauseEdges[index] = edge with { EndTick = Tick, Magnitude = Math.Max(1, patient.WaitTicks) };
                return;
            }
        }
    }

    private static string RootCauseSourceFor(PatientStage stage) => stage switch
    {
        PatientStage.WaitingReception or PatientStage.InReception => "接待台",
        PatientStage.WaitingTriage or PatientStage.InTriage => "分诊台",
        PatientStage.WaitingConsult or PatientStage.InConsult => "全科门诊.初诊",
        PatientStage.WaitingLabSampling or PatientStage.InLabSampling => "基础化验.采样",
        PatientStage.WaitingLabAnalysis or PatientStage.InLabAnalysis => "基础化验.分析",
        PatientStage.Outsourced => "基础化验.外送",
        PatientStage.ResultPending => "基础化验.结果发布",
        PatientStage.WaitingFollowUp or PatientStage.InFollowUp => "全科门诊.结果复诊",
        PatientStage.WaitingTreatment or PatientStage.InTreatment => "治疗室",
        PatientStage.WaitingPharmacy or PatientStage.InPharmacy => "药房",
        _ => "医院流程",
    };

    private PatientSnapshot ToSnapshot(PatientState patient) => new(
        patient.PatientId,
        patient.CaseId,
        patient.Stage,
        patient.WaitTicks,
        patient.PatienceBasisPoints,
        patient.RiskBasisPoints,
        RiskLevelFor(patient.RiskBasisPoints),
        patient.DiagnosisConfidenceBasisPoints,
        patient.RequiresLab,
        patient.RequiresTreatment);

    private static bool IsWaiting(PatientStage stage) => stage is
        PatientStage.WaitingReception or PatientStage.WaitingTriage or PatientStage.WaitingConsult or
        PatientStage.WaitingLabSampling or PatientStage.WaitingLabAnalysis or PatientStage.ResultPending or
        PatientStage.Outsourced or PatientStage.WaitingFollowUp or PatientStage.WaitingTreatment or
        PatientStage.WaitingPharmacy;

    private static bool IsTerminal(PatientState patient) => patient.Stage is
        PatientStage.Completed or PatientStage.Transferred or PatientStage.LeftWithoutCare or PatientStage.LeftUnfinished;

    private static RiskLevel RiskLevelFor(int riskBasisPoints) => riskBasisPoints switch
    {
        < 1_000 => RiskLevel.Routine,
        < 3_500 => RiskLevel.Low,
        < 6_500 => RiskLevel.Medium,
        < 8_500 => RiskLevel.High,
        _ => RiskLevel.Critical,
    };

    private sealed class PatientState
    {
        public PatientState(
            long numericId,
            string patientId,
            int arrivalTick,
            string caseId,
            bool requiresLab,
            bool requiresTreatment,
            int riskBasisPoints,
            int diagnosisConfidenceBasisPoints,
            long revenueMinorUnits)
        {
            NumericId = numericId;
            PatientId = patientId;
            ArrivalTick = arrivalTick;
            CaseId = caseId;
            RequiresLab = requiresLab;
            RequiresTreatment = requiresTreatment;
            RiskBasisPoints = riskBasisPoints;
            Diagnosis = M1CaseCatalog.TryGet(caseId, out var definition)
                ? DiagnosisConfidenceCalculator.Calculate(definition.Candidates, Array.Empty<DiagnosisEvidence>())
                : new DiagnosisAssessment(
                    Math.Clamp(diagnosisConfidenceBasisPoints, 0, 10_000),
                    caseId,
                    Array.AsReadOnly(new[]
                    {
                        new DiagnosisSupport(caseId, Math.Clamp(diagnosisConfidenceBasisPoints, 0, 10_000)),
                    }));
            PatienceBasisPoints = 10_000;
            RevenueMinorUnits = revenueMinorUnits;
        }

        public long NumericId { get; }
        public string PatientId { get; }
        public string CaseId { get; }
        public int ArrivalTick { get; }
        public bool RequiresLab { get; }
        public bool RequiresTreatment { get; }
        public int RiskBasisPoints { get; set; }
        public int DiagnosisConfidenceBasisPoints => Diagnosis.ConfidenceBasisPoints;
        public DiagnosisAssessment Diagnosis { get; private set; }
        public bool LabEvidenceApplied { get; private set; }
        public int PatienceBasisPoints { get; set; }
        public int WaitTicks { get; set; }
        public int TotalWaitingTicks { get; set; }
        public int StageEnteredTick { get; set; }
        public int OutsourceReturnTick { get; set; }
        public long RevenueMinorUnits { get; }
        public PatientStage Stage { get; set; }

        public void ApplyLabEvidence(IReadOnlyList<DiagnosisEvidence> evidence)
        {
            if (LabEvidenceApplied)
            {
                return;
            }

            if (!M1CaseCatalog.TryGet(CaseId, out var definition))
            {
                return;
            }

            Diagnosis = DiagnosisConfidenceCalculator.Calculate(definition.Candidates, evidence);
            LabEvidenceApplied = true;
        }
    }

    private sealed record CaseDefinition(
        string CaseId,
        bool RequiresLab,
        bool RequiresTreatment,
        int BaseRiskBasisPoints,
        int DiagnosisConfidenceBasisPoints,
        long RevenueMinorUnits);
}
