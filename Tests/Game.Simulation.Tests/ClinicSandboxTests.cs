using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class ClinicSandboxTests
{
    [Fact]
    public void PatientEntersThenWaitsForReception()
    {
        var clinic = new ClinicSandbox();

        Assert.Equal(PatientLocation.Entrance, clinic.Snapshot.PatientLocation);

        clinic.AdvanceTime();

        Assert.Equal(PatientLocation.ReceptionQueue, clinic.Snapshot.PatientLocation);
        Assert.Equal(1, clinic.Snapshot.WaitingPatients);
        Assert.Equal("接待等待", clinic.Snapshot.CurrentBottleneck);
    }

    [Fact]
    public void WaitingRemainsVisibleUntilReceptionStarts()
    {
        var clinic = new ClinicSandbox();
        clinic.AdvanceTime();

        clinic.AdvanceTime();
        clinic.AdvanceTime();

        Assert.Equal(PatientLocation.ReceptionQueue, clinic.Snapshot.PatientLocation);
        Assert.Equal(2, clinic.Snapshot.PatientWaitingTicks);
        Assert.Equal(1, clinic.Snapshot.WaitingPatients);
    }

    [Fact]
    public void PlayerActionsCompleteTheClinicJourneyWithoutSkippingSteps()
    {
        var clinic = new ClinicSandbox();
        clinic.AdvanceTime();

        Assert.True(clinic.StartReception());
        Assert.Equal(PatientLocation.InReception, clinic.Snapshot.PatientLocation);
        Assert.True(clinic.Snapshot.ReceptionBusy);

        clinic.AdvanceTime();
        Assert.Equal(PatientLocation.DoctorQueue, clinic.Snapshot.PatientLocation);
        Assert.Equal("医生等待", clinic.Snapshot.CurrentBottleneck);

        Assert.True(clinic.StartDoctorVisit());
        Assert.Equal(PatientLocation.InDoctorRoom, clinic.Snapshot.PatientLocation);
        Assert.True(clinic.Snapshot.DoctorBusy);

        clinic.AdvanceTime();
        Assert.Equal(PatientLocation.VisitCompleted, clinic.Snapshot.PatientLocation);
        Assert.Equal(1, clinic.Snapshot.CompletedVisits);

        clinic.AdvanceTime();
        Assert.Equal(PatientLocation.Exit, clinic.Snapshot.PatientLocation);
        Assert.Equal(1, clinic.Snapshot.DepartedPatients);
        Assert.Equal(0, clinic.Snapshot.PatientsInClinic);
    }

    [Fact]
    public void InvalidActionsCannotSkipOrRepeatTheJourney()
    {
        var clinic = new ClinicSandbox();

        Assert.False(clinic.StartDoctorVisit());
        Assert.False(clinic.StartReception());
        Assert.Equal(PatientLocation.Entrance, clinic.Snapshot.PatientLocation);

        clinic.AdvanceTime();
        Assert.True(clinic.StartReception());
        Assert.False(clinic.StartReception());
    }

    [Fact]
    public void DashboardShowsLayoutHospitalStateAndReadableTimeline()
    {
        var clinic = new ClinicSandbox();
        clinic.AdvanceTime();

        var dashboard = ClinicDashboard.Render(clinic.Snapshot, clinic.Events);

        Assert.Contains("诊所布局", dashboard);
        Assert.Contains("入口", dashboard);
        Assert.Contains("接待区", dashboard);
        Assert.Contains("候诊区", dashboard);
        Assert.Contains("医生诊室", dashboard);
        Assert.Contains("出口", dashboard);
        Assert.Contains("医院状态", dashboard);
        Assert.Contains("当前瓶颈：接待等待", dashboard);
        Assert.Contains("事件轨迹", dashboard);
        Assert.Contains("T00 | 患者进入 | 状态：已到达医院", dashboard);
        Assert.Contains("T01 | 患者开始等待 | 状态：等待接待", dashboard);
    }

    [Fact]
    public void EventTimelineRecordsTheCompletePatientExperienceInOrder()
    {
        var clinic = new ClinicSandbox();
        clinic.AdvanceTime();
        clinic.StartReception();
        clinic.AdvanceTime();
        clinic.StartDoctorVisit();
        clinic.AdvanceTime();
        clinic.AdvanceTime();

        Assert.Equal(
            new[]
            {
                "患者进入",
                "患者开始等待",
                "接待开始",
                "登记完成",
                "患者开始等待",
                "接诊开始",
                "诊疗完成",
                "患者离开"
            },
            clinic.Events.Select(entry => entry.Event));
    }

    [Fact]
    public void QueueScenarioShowsTwoPatientsAndAReceptionBottleneck()
    {
        var clinic = ClinicSandbox.CreateQueueScenario();
        clinic.AdvanceTime();

        Assert.Equal(2, clinic.Snapshot.PatientsInClinic);
        Assert.Equal(2, clinic.Snapshot.WaitingPatients);
        Assert.Equal("接待等待", clinic.Snapshot.CurrentBottleneck);
        Assert.Equal(new[] { "患者001", "患者002" }, clinic.Snapshot.Patients.Select(patient => patient.PatientId));

        var dashboard = ClinicDashboard.Render(clinic.Snapshot, clinic.Events);
        Assert.Contains("患者001", dashboard);
        Assert.Contains("患者002", dashboard);
        Assert.Contains("等待患者：2", dashboard);
    }

    [Fact]
    public void OneReceptionistAndOneDoctorCreateVisibleQueuePressure()
    {
        var clinic = ClinicSandbox.CreateQueueScenario();
        clinic.AdvanceTime();

        Assert.True(clinic.StartReception());
        clinic.AdvanceTime();

        Assert.Equal(PatientLocation.DoctorQueue, clinic.Snapshot.Patients.Single(patient => patient.PatientId == "患者001").Location);
        Assert.Equal(PatientLocation.ReceptionQueue, clinic.Snapshot.Patients.Single(patient => patient.PatientId == "患者002").Location);
        Assert.Equal(2, clinic.Snapshot.WaitingPatients);
        Assert.Equal("接待等待", clinic.Snapshot.CurrentBottleneck);

        Assert.True(clinic.StartDoctorVisit());
        Assert.True(clinic.StartReception());

        Assert.True(clinic.Snapshot.DoctorBusy);
        Assert.True(clinic.Snapshot.ReceptionBusy);
        Assert.Equal(0, clinic.Snapshot.WaitingPatients);
    }
}
