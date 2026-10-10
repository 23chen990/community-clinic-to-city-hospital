using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class ConfigValidationTests
{
    [Fact]
    public void DefaultConfigurationIsValid()
    {
        new M1SimulationConfig().Validate();
    }

    [Theory]
    [InlineData(nameof(M1SimulationConfig.ShiftLengthTicks))]
    [InlineData(nameof(M1SimulationConfig.TemporarySupportDurationTicks))]
    [InlineData(nameof(M1SimulationConfig.TemporarySupportPublishIntervalTicks))]
    [InlineData(nameof(M1SimulationConfig.RiskPriorityPublishIntervalTicks))]
    [InlineData(nameof(M1SimulationConfig.NormalPublishIntervalTicks))]
    [InlineData(nameof(M1SimulationConfig.OutsourceReturnTicks))]
    [InlineData(nameof(M1SimulationConfig.LabAnalysisServiceTicks))]
    [InlineData(nameof(M1SimulationConfig.ServiceTicks))]
    [InlineData(nameof(M1SimulationConfig.TreatmentServiceTicks))]
    [InlineData(nameof(M1SimulationConfig.CriticalTransferWaitTicks))]
    public void ServiceDurationAndIntervalFieldsMustBePositive(string fieldName)
    {
        AssertInvalid(CreateWith(fieldName, 0), fieldName);
    }

    [Theory]
    [InlineData(nameof(M1SimulationConfig.OutsourceCostPerSampleMinorUnits))]
    [InlineData(nameof(M1SimulationConfig.InitialCashMinorUnits))]
    public void CostAndBalanceFieldsCannotBeNegative(string fieldName)
    {
        AssertInvalid(CreateWith(fieldName, -1), fieldName);
    }

    [Theory]
    [InlineData(nameof(M1SimulationConfig.TemporarySupportFatigueBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.FatigueDurationPenaltyBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.PatientPatienceBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.PatienceLossPerWaitingTick))]
    [InlineData(nameof(M1SimulationConfig.RiskIncreasePerWaitingTick))]
    [InlineData(nameof(M1SimulationConfig.DiagnosisConfidenceThreshold))]
    [InlineData(nameof(M1SimulationConfig.ReturnBaseChanceBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.ReturnPatienceWeightBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.ReturnEvidenceBonusBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.ReturnWaitPenaltyPerTickBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.MinReturnChanceBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.MaxReturnChanceBasisPoints))]
    [InlineData(nameof(M1SimulationConfig.ReturningPatienceBasisPoints))]
    public void BasisPointAndPatienceFieldsMustStayWithinZeroToTenThousand(string fieldName)
    {
        AssertInvalid(CreateWith(fieldName, -1), fieldName);
        AssertInvalid(CreateWith(fieldName, 10_001), fieldName);
    }

    [Fact]
    public void ReturnChanceMinimumCannotExceedMaximum()
    {
        AssertInvalid(
            new M1SimulationConfig
            {
                MinReturnChanceBasisPoints = 9_001,
                MaxReturnChanceBasisPoints = 9_000,
            },
            nameof(M1SimulationConfig.MinReturnChanceBasisPoints));
    }

    [Fact]
    public void OutsourceCapacityMustBePositive()
    {
        AssertInvalid(
            new M1SimulationConfig { MaxOutsourceSamples = 0 },
            nameof(M1SimulationConfig.MaxOutsourceSamples));
    }

    [Theory]
    [InlineData(nameof(M1SimulationConfig.ClosingDurationTicks))]
    [InlineData(nameof(M1SimulationConfig.ClosingExtensionTicks))]
    public void ClosingDurationsMayBeZeroButCannotBeNegative(string fieldName)
    {
        AssertInvalid(CreateWith(fieldName, -1), fieldName);
    }

    [Fact]
    public void ReturnArrivalDelayMustBeAtLeastOneTick()
    {
        AssertInvalid(
            new M1SimulationConfig { ReturnArrivalDelayTicks = 0 },
            nameof(M1SimulationConfig.ReturnArrivalDelayTicks));
    }

    // CURRENT §5.6: a scheduled return must be reachable during the next
    // operating day, rather than silently disappearing during Closing.
    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(121)]
    public void ReturnArrivalMustBeBeforeNextDaysClosingStarts(int arrivalTick)
    {
        AssertInvalid(
            new M1SimulationConfig
            {
                ShiftLengthTicks = 100,
                ClosingDurationTicks = 20,
                ReturnArrivalDelayTicks = arrivalTick,
            },
            nameof(M1SimulationConfig.ReturnArrivalDelayTicks));
    }

    [Fact]
    public void ValidationDoesNotImposeAUnspecifiedMinimumShiftLength()
    {
        new M1SimulationConfig
        {
            ShiftLengthTicks = 2,
            ClosingDurationTicks = 0,
            ClosingExtensionTicks = 0,
            ReturnArrivalDelayTicks = 1,
        }.Validate();
    }

    [Fact]
    public void HospitalConstructionValidatesTheConfigurationBeforeRunning()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            HospitalSimulation.CreateBaseline(
                seed: 1,
                config: new M1SimulationConfig { ServiceTicks = 0 }));

        Assert.Equal(nameof(M1SimulationConfig.ServiceTicks), exception.ParamName);
    }

    private static void AssertInvalid(M1SimulationConfig config, string expectedParameter)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(config.Validate);
        Assert.Equal(expectedParameter, exception.ParamName);
    }

    private static M1SimulationConfig CreateWith(string fieldName, int value) => fieldName switch
    {
        nameof(M1SimulationConfig.ShiftLengthTicks) => new() { ShiftLengthTicks = value },
        nameof(M1SimulationConfig.TemporarySupportDurationTicks) => new() { TemporarySupportDurationTicks = value },
        nameof(M1SimulationConfig.TemporarySupportPublishIntervalTicks) => new() { TemporarySupportPublishIntervalTicks = value },
        nameof(M1SimulationConfig.RiskPriorityPublishIntervalTicks) => new() { RiskPriorityPublishIntervalTicks = value },
        nameof(M1SimulationConfig.NormalPublishIntervalTicks) => new() { NormalPublishIntervalTicks = value },
        nameof(M1SimulationConfig.OutsourceReturnTicks) => new() { OutsourceReturnTicks = value },
        nameof(M1SimulationConfig.LabAnalysisServiceTicks) => new() { LabAnalysisServiceTicks = value },
        nameof(M1SimulationConfig.ServiceTicks) => new() { ServiceTicks = value },
        nameof(M1SimulationConfig.TreatmentServiceTicks) => new() { TreatmentServiceTicks = value },
        nameof(M1SimulationConfig.CriticalTransferWaitTicks) => new() { CriticalTransferWaitTicks = value },
        nameof(M1SimulationConfig.OutsourceCostPerSampleMinorUnits) => new() { OutsourceCostPerSampleMinorUnits = value },
        nameof(M1SimulationConfig.InitialCashMinorUnits) => new() { InitialCashMinorUnits = value },
        nameof(M1SimulationConfig.TemporarySupportFatigueBasisPoints) => new() { TemporarySupportFatigueBasisPoints = value },
        nameof(M1SimulationConfig.FatigueDurationPenaltyBasisPoints) => new() { FatigueDurationPenaltyBasisPoints = value },
        nameof(M1SimulationConfig.PatientPatienceBasisPoints) => new() { PatientPatienceBasisPoints = value },
        nameof(M1SimulationConfig.PatienceLossPerWaitingTick) => new() { PatienceLossPerWaitingTick = value },
        nameof(M1SimulationConfig.RiskIncreasePerWaitingTick) => new() { RiskIncreasePerWaitingTick = value },
        nameof(M1SimulationConfig.DiagnosisConfidenceThreshold) => new() { DiagnosisConfidenceThreshold = value },
        nameof(M1SimulationConfig.ReturnBaseChanceBasisPoints) => new() { ReturnBaseChanceBasisPoints = value },
        nameof(M1SimulationConfig.ReturnPatienceWeightBasisPoints) => new() { ReturnPatienceWeightBasisPoints = value },
        nameof(M1SimulationConfig.ReturnEvidenceBonusBasisPoints) => new() { ReturnEvidenceBonusBasisPoints = value },
        nameof(M1SimulationConfig.ReturnWaitPenaltyPerTickBasisPoints) => new() { ReturnWaitPenaltyPerTickBasisPoints = value },
        nameof(M1SimulationConfig.MinReturnChanceBasisPoints) => new() { MinReturnChanceBasisPoints = value },
        nameof(M1SimulationConfig.MaxReturnChanceBasisPoints) => new() { MaxReturnChanceBasisPoints = value },
        nameof(M1SimulationConfig.ReturningPatienceBasisPoints) => new() { ReturningPatienceBasisPoints = value },
        nameof(M1SimulationConfig.ClosingDurationTicks) => new() { ClosingDurationTicks = value },
        nameof(M1SimulationConfig.ClosingExtensionTicks) => new() { ClosingExtensionTicks = value },
        _ => throw new ArgumentOutOfRangeException(nameof(fieldName), fieldName, "测试字段未覆盖。"),
    };
}
