using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class HospitalVerticalSliceTests
{
    [Fact]
    public void PatientsArriveThroughReceptionTriageAndConsultation()
    {
        var slice = new HospitalVerticalSlice();

        slice.AdvanceTime(4);

        Assert.Equal(3, slice.Snapshot.ArrivedPatients);
        Assert.Equal(VerticalPatientStage.LabSampling, Patient(slice, "患者001").Stage);
        Assert.Equal(VerticalPatientStage.Consultation, Patient(slice, "患者002").Stage);
        Assert.Equal(VerticalPatientStage.Triage, Patient(slice, "患者003").Stage);
        Assert.Contains(slice.Events, entry => entry.Event == "进入医院");
        Assert.Contains(slice.Events, entry => entry.Event == "登记完成");
        Assert.Contains(slice.Events, entry => entry.Event == "分诊完成");
        Assert.Contains(slice.Events, entry => entry.Event == "接诊开始");
    }

    [Fact]
    public void LabHasSamplingAnalysisAndResultPublicationBacklog()
    {
        var slice = new HospitalVerticalSlice();

        slice.AdvanceTime(6);

        Assert.Equal(VerticalPatientStage.ResultPending, Patient(slice, "患者001").Stage);
        Assert.Equal(1, slice.Snapshot.ResultBacklog);
        Assert.Equal("结果发布", slice.Snapshot.CurrentBottleneck);
        Assert.Equal("结果发布积压 → 复诊等待", slice.Snapshot.RootCause);
        Assert.Contains(slice.Events, entry => entry.Event == "采样完成");
        Assert.Contains(slice.Events, entry => entry.Event == "分析完成");
        Assert.Contains(slice.Events, entry => entry.Event == "结果待发布");
    }

    [Fact]
    public void TemporarySupportReturnsResultToConsultation()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(6);

        Assert.True(slice.StartTemporarySupport());
        Assert.True(slice.Snapshot.TemporarySupportActive);

        slice.AdvanceTime(1);

        Assert.Equal(VerticalPatientStage.FollowUp, Patient(slice, "患者001").Stage);
        Assert.Equal(0, slice.Snapshot.ResultBacklog);
        Assert.Contains(slice.Events, entry => entry.Event == "结果发布");
        Assert.Contains(slice.Events, entry => entry.Event == "结果返回门诊");
    }

    [Fact]
    public void OutsourcedSampleReturnsLaterWithoutUsingTemporarySupport()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(6);

        Assert.True(slice.StartOutsourcedSample());
        Assert.False(slice.Snapshot.TemporarySupportActive);
        Assert.True(slice.Snapshot.OutsourcingActive);
        Assert.Equal(1, slice.Snapshot.ResultBacklog);

        slice.AdvanceTime(1);
        Assert.Equal(VerticalPatientStage.ResultPending, Patient(slice, "患者001").Stage);
        Assert.Equal(1, slice.Snapshot.ResultBacklog);

        slice.AdvanceTime(1);
        Assert.Equal(VerticalPatientStage.FollowUp, Patient(slice, "患者001").Stage);
        Assert.Equal(0, slice.Snapshot.ResultBacklog);
        Assert.Contains(slice.Events, entry => entry.Event == "外送样本开始");
        Assert.Contains(slice.Events, entry => entry.Event == "外送结果返回");
    }

    [Fact]
    public void OutsourcedResultCompletesFollowUpTreatmentPharmacyAndDischarge()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(6);
        Assert.True(slice.StartOutsourcedSample());

        slice.AdvanceTime(2);
        Assert.Equal(VerticalPatientStage.FollowUp, Patient(slice, "患者001").Stage);

        slice.AdvanceTime(1);
        Assert.Equal(VerticalPatientStage.Treatment, Patient(slice, "患者001").Stage);

        slice.AdvanceTime(1);
        Assert.Equal(VerticalPatientStage.Pharmacy, Patient(slice, "患者001").Stage);

        slice.AdvanceTime(1);
        Assert.Equal(VerticalPatientStage.Discharged, Patient(slice, "患者001").Stage);
        Assert.True(slice.Snapshot.ShiftComplete);
    }

    [Fact]
    public void TemporarySupportAndOutsourcingAreAlternativeChoices()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(6);

        Assert.True(slice.StartTemporarySupport());
        Assert.False(slice.StartOutsourcedSample());
    }

    [Fact]
    public void TemporarySupportPublishesResultAfterBeingEnabledLate()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(11);

        Assert.True(slice.StartTemporarySupport());
        Assert.Equal(VerticalPatientStage.ResultPending, Patient(slice, "患者001").Stage);

        slice.AdvanceTime(1);

        Assert.Equal(VerticalPatientStage.FollowUp, Patient(slice, "患者001").Stage);
        Assert.Equal(0, slice.Snapshot.ResultBacklog);
        Assert.Equal("无", slice.Snapshot.CurrentBottleneck);
        Assert.Equal(1, slice.Events.Count(entry => entry.Event == "结果发布"));
    }

    [Fact]
    public void TemporarySupportCannotBeEnabledTwiceForTheSameBacklog()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(6);

        Assert.True(slice.StartTemporarySupport());
        Assert.False(slice.StartTemporarySupport());
        Assert.Equal(1, slice.Events.Count(entry => entry.Event == "临时支援开始"));
        Assert.Equal(1, slice.Snapshot.Summary.TemporarySupportUsed);
    }

    [Fact]
    public void LateTemporarySupportStillCompletesThePatientJourney()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(11);

        Assert.True(slice.StartTemporarySupport());

        slice.AdvanceTime(4);

        Assert.Equal(VerticalPatientStage.Discharged, Patient(slice, "患者001").Stage);
        Assert.True(slice.Snapshot.ShiftComplete);
        Assert.Equal(3, slice.Snapshot.Summary.DischargedPatients);
    }

    [Fact]
    public void PatientsCompleteTreatmentPharmacyDischargeAndShiftSettlement()
    {
        var slice = new HospitalVerticalSlice();
        slice.AdvanceTime(6);
        Assert.True(slice.StartTemporarySupport());

        slice.AdvanceTime(4);

        Assert.All(slice.Snapshot.Patients, patient => Assert.Equal(VerticalPatientStage.Discharged, patient.Stage));
        Assert.True(slice.Snapshot.ShiftComplete);
        Assert.Equal(3, slice.Snapshot.Summary.DischargedPatients);
        Assert.Equal(1, slice.Snapshot.Summary.PublishedResults);
        Assert.Contains(slice.Events, entry => entry.Event == "治疗完成");
        Assert.Contains(slice.Events, entry => entry.Event == "药房完成");
        Assert.Contains(slice.Events, entry => entry.Event == "患者离开");
    }

    private static VerticalPatientView Patient(HospitalVerticalSlice slice, string patientId)
    {
        return slice.Snapshot.Patients.Single(patient => patient.PatientId == patientId);
    }
}
