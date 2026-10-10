namespace Game.Simulation;

public sealed partial class HospitalSimulation
{
    private CommandResult ApplyDispatch(SimulationCommand command)
    {
        if (dispatchPlan != LabDispatchPlan.None)
        {
            return RejectDispatch(command, "已有化验调度方案生效", CommandResultStatus.RejectedInvalid);
        }

        switch (command.DispatchPlan)
        {
            case LabDispatchPlan.RiskPriority:
                if (resultQueue.Count == 0)
                {
                    return RejectDispatch(command, "没有可调度的结果积压", CommandResultStatus.RejectedInvalid);
                }

                dispatchPlan = LabDispatchPlan.RiskPriority;
                AddEvent(SimulationEventType.RiskPriorityStarted, "医院", "按风险优先发布结果");
                return new CommandResult(CommandResultStatus.Accepted, command.CommandId, "按风险优先发布结果");
            case LabDispatchPlan.TemporarySupport:
                if (resultQueue.Count == 0)
                {
                    return RejectDispatch(command, "没有可调度的结果积压", CommandResultStatus.RejectedInvalid);
                }

                dispatchPlan = LabDispatchPlan.TemporarySupport;
                supportUntilTick = Tick + config.TemporarySupportDurationTicks;
                nextResultPublishTick = Tick + config.TemporarySupportPublishIntervalTicks;
                technicianFatigueBasisPoints = Math.Min(
                    10_000,
                    technicianFatigueBasisPoints + config.TemporarySupportFatigueBasisPoints);
                AddEvent(SimulationEventType.TemporarySupportStarted, "医院", "临时支援开始");
                return new CommandResult(CommandResultStatus.Accepted, command.CommandId, "临时支援开始");
            case LabDispatchPlan.OutsourceSamples:
                return StartOutsourcing(command);
            default:
                return RejectDispatch(command, "无效的化验调度方案", CommandResultStatus.RejectedInvalid);
        }
    }

    private CommandResult StartOutsourcing(SimulationCommand command)
    {
        if (config.MaxOutsourceSamples <= 0 || config.OutsourceCostPerSampleMinorUnits < 0)
        {
            return RejectDispatch(command, "外送配置无效", CommandResultStatus.RejectedInvalid);
        }

        var selected = queues[ServiceKind.LabAnalysis]
            .Where(patient => patient.Stage == PatientStage.WaitingLabAnalysis && patient.RiskBasisPoints < 3_500)
            .OrderBy(patient => patient.RiskBasisPoints)
            .ThenBy(patient => patient.StageEnteredTick)
            .ThenBy(patient => patient.NumericId)
            .Take(config.MaxOutsourceSamples)
            .ToArray();
        if (selected.Length == 0)
        {
            return RejectDispatch(command, "没有可外送的普通未分析样本", CommandResultStatus.RejectedInvalid);
        }

        var totalCost = checked((long)selected.Length * config.OutsourceCostPerSampleMinorUnits);
        if (AvailableCashMinorUnits() < totalCost)
        {
            return RejectDispatch(command, "外送费用不足", CommandResultStatus.RejectedInsufficient);
        }

        dispatchPlan = LabDispatchPlan.OutsourceSamples;
        foreach (var patient in selected)
        {
            queues[ServiceKind.LabAnalysis].Remove(patient);
            patient.Stage = PatientStage.Outsourced;
            patient.WaitTicks = 0;
            patient.OutsourceReturnTick = Tick + config.OutsourceReturnTicks;
            outsourcedQueue.Add(patient);
            ledgerEntries.Add(new LedgerEntry(
                $"dispatch:{command.CommandId}:outsource:{patient.PatientId}",
                LedgerKind.DispatchCost,
                config.OutsourceCostPerSampleMinorUnits,
                Tick,
                patient.PatientId));
            AddEvent(SimulationEventType.OutsourcedSampleStarted, patient.PatientId, "普通样本外送");
        }

        return new CommandResult(CommandResultStatus.Accepted, command.CommandId, $"已外送 {selected.Length} 份普通样本");
    }

    private CommandResult RejectDispatch(
        SimulationCommand command,
        string reason,
        CommandResultStatus status)
    {
        AddEvent(SimulationEventType.CommandRejected, "医院", reason);
        return new CommandResult(status, command.CommandId, reason);
    }

    private long AvailableCashMinorUnits()
    {
        return config.InitialCashMinorUnits
            + ledgerEntries
                .Where(entry => entry.Kind is LedgerKind.PatientRevenue or LedgerKind.Refund)
                .Sum(entry => entry.AmountMinorUnits)
            - ledgerEntries
                .Where(entry => entry.Kind == LedgerKind.DispatchCost)
                .Sum(entry => entry.AmountMinorUnits);
    }

    private void ProcessOutsourcedResults()
    {
        foreach (var patient in outsourcedQueue
                     .Where(patient => patient.OutsourceReturnTick <= Tick)
                     .OrderBy(patient => patient.NumericId)
                     .ToArray())
        {
            outsourcedQueue.Remove(patient);
            patient.Stage = PatientStage.WaitingFollowUp;
            patient.WaitTicks = 0;
            ApplyLabEvidence(patient);
            EnqueueRootCause(patient, RootCauseCode.ResultReturnedToFollowUp);
            AddEvent(SimulationEventType.OutsourcedResultReturned, patient.PatientId, "外送结果返回");
            AddEvent(SimulationEventType.ResultReturnedToConsultation, patient.PatientId, "结果返回门诊");
            Enqueue(ServiceKind.FollowUp, patient);
        }

        if (dispatchPlan == LabDispatchPlan.OutsourceSamples && outsourcedQueue.Count == 0)
        {
            dispatchPlan = LabDispatchPlan.None;
        }
    }

    private void ProcessResultPublication()
    {
        if (resultQueue.Count == 0)
        {
            EndTemporarySupportIfNeeded();
            if (dispatchPlan == LabDispatchPlan.RiskPriority)
            {
                dispatchPlan = LabDispatchPlan.None;
            }

            return;
        }

        if (Tick < nextResultPublishTick)
        {
            EndTemporarySupportIfNeeded();
            return;
        }

        var patient = dispatchPlan == LabDispatchPlan.RiskPriority
            ? resultQueue
                .OrderByDescending(candidate => candidate.RiskBasisPoints)
                .ThenBy(candidate => candidate.StageEnteredTick)
                .ThenBy(candidate => candidate.NumericId)
                .First()
            : resultQueue.OrderBy(candidate => candidate.StageEnteredTick).ThenBy(candidate => candidate.NumericId).First();
        resultQueue.Remove(patient);
        patient.Stage = PatientStage.WaitingFollowUp;
        patient.WaitTicks = 0;
        ApplyLabEvidence(patient);
        EnqueueRootCause(patient, RootCauseCode.ResultReturnedToFollowUp);
        AddEvent(SimulationEventType.ResultPublished, patient.PatientId, "结果发布");
        AddEvent(SimulationEventType.ResultReturnedToConsultation, patient.PatientId, "结果返回门诊");
        Enqueue(ServiceKind.FollowUp, patient);

        var interval = dispatchPlan switch
        {
            LabDispatchPlan.TemporarySupport => config.TemporarySupportPublishIntervalTicks,
            _ => ApplyFatigueDuration(config.NormalPublishIntervalTicks),
        };
        nextResultPublishTick = Tick + interval;
        EndTemporarySupportIfNeeded();
    }

    private void EndTemporarySupportIfNeeded()
    {
        if (dispatchPlan == LabDispatchPlan.TemporarySupport && Tick >= supportUntilTick)
        {
            dispatchPlan = LabDispatchPlan.None;
            AddEvent(SimulationEventType.TemporarySupportEnded, "医院", "临时支援结束");
            nextResultPublishTick = Tick + ApplyFatigueDuration(config.NormalPublishIntervalTicks);
        }
    }

    private int ApplyFatigueDuration(int ticks)
    {
        if (ticks <= 0 || technicianFatigueBasisPoints <= 0 || config.FatigueDurationPenaltyBasisPoints <= 0)
        {
            return ticks;
        }

        var numerator = (long)ticks * technicianFatigueBasisPoints * config.FatigueDurationPenaltyBasisPoints;
        var extraTicks = (numerator + 99_999_999) / 100_000_000;
        return checked(ticks + (int)extraTicks);
    }
}
