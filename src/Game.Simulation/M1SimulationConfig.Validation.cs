namespace Game.Simulation;

public sealed partial record M1SimulationConfig
{
    /// <summary>
    /// Validates the experiment configuration before it enters authoritative
    /// simulation state.  The checks deliberately enforce only domain bounds;
    /// they do not impose a minimum scenario length or other fixture-specific
    /// tuning assumptions.
    /// </summary>
    public void Validate()
    {
        ValidatePositive(nameof(ShiftLengthTicks), ShiftLengthTicks);
        ValidatePositive(nameof(TemporarySupportDurationTicks), TemporarySupportDurationTicks);
        ValidatePositive(nameof(TemporarySupportPublishIntervalTicks), TemporarySupportPublishIntervalTicks);
        ValidatePositive(nameof(RiskPriorityPublishIntervalTicks), RiskPriorityPublishIntervalTicks);
        ValidatePositive(nameof(NormalPublishIntervalTicks), NormalPublishIntervalTicks);
        ValidatePositive(nameof(OutsourceReturnTicks), OutsourceReturnTicks);
        ValidatePositive(nameof(LabAnalysisServiceTicks), LabAnalysisServiceTicks);
        ValidatePositive(nameof(ServiceTicks), ServiceTicks);
        ValidatePositive(nameof(TreatmentServiceTicks), TreatmentServiceTicks);
        ValidatePositive(nameof(CriticalTransferWaitTicks), CriticalTransferWaitTicks);

        ValidateNonNegative(nameof(OutsourceCostPerSampleMinorUnits), OutsourceCostPerSampleMinorUnits);
        ValidateNonNegative(nameof(InitialCashMinorUnits), InitialCashMinorUnits);

        ValidateBasisPoints(nameof(TemporarySupportFatigueBasisPoints), TemporarySupportFatigueBasisPoints);
        ValidateBasisPoints(nameof(FatigueDurationPenaltyBasisPoints), FatigueDurationPenaltyBasisPoints);
        ValidateBasisPoints(nameof(PatientPatienceBasisPoints), PatientPatienceBasisPoints);
        ValidateBasisPoints(nameof(PatienceLossPerWaitingTick), PatienceLossPerWaitingTick);
        ValidateBasisPoints(nameof(RiskIncreasePerWaitingTick), RiskIncreasePerWaitingTick);
        ValidateBasisPoints(nameof(DiagnosisConfidenceThreshold), DiagnosisConfidenceThreshold);
        ValidateBasisPoints(nameof(ReturnBaseChanceBasisPoints), ReturnBaseChanceBasisPoints);
        ValidateBasisPoints(nameof(ReturnPatienceWeightBasisPoints), ReturnPatienceWeightBasisPoints);
        ValidateBasisPoints(nameof(ReturnEvidenceBonusBasisPoints), ReturnEvidenceBonusBasisPoints);
        ValidateBasisPoints(nameof(ReturnWaitPenaltyPerTickBasisPoints), ReturnWaitPenaltyPerTickBasisPoints);
        ValidateBasisPoints(nameof(MinReturnChanceBasisPoints), MinReturnChanceBasisPoints);
        ValidateBasisPoints(nameof(MaxReturnChanceBasisPoints), MaxReturnChanceBasisPoints);
        ValidateBasisPoints(nameof(ReturningPatienceBasisPoints), ReturningPatienceBasisPoints);

        if (MinReturnChanceBasisPoints > MaxReturnChanceBasisPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinReturnChanceBasisPoints),
                MinReturnChanceBasisPoints,
                "最低返诊概率不能高于最高返诊概率。");
        }

        if (MaxOutsourceSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutsourceSamples),
                MaxOutsourceSamples,
                "外送样本上限必须大于 0。");
        }

        ValidateNonNegative(nameof(ClosingDurationTicks), ClosingDurationTicks);
        ValidateNonNegative(nameof(ClosingExtensionTicks), ClosingExtensionTicks);

        if (ReturnArrivalDelayTicks < 1 || ReturnArrivalDelayTicks >= ShiftLengthTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReturnArrivalDelayTicks),
                ReturnArrivalDelayTicks,
                "返诊必须在下一营业日开诊后、收尾开始前到达。");
        }
    }

    private static void ValidatePositive(string parameterName, int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "必须大于 0。");
        }
    }

    private static void ValidateNonNegative(string parameterName, long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "不能为负数。");
        }
    }

    private static void ValidateBasisPoints(string parameterName, int value)
    {
        if (value is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "比例、耐心和阈值必须位于 0 到 10000 之间。");
        }
    }
}
