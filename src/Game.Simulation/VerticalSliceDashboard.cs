using System.Text;

namespace Game.Simulation;

public static class VerticalSliceDashboard
{
    public static string Render(VerticalSliceSnapshot snapshot, IReadOnlyList<VerticalEvent> events)
    {
        var output = new StringBuilder();
        output.AppendLine($"M1-A 第一个完整经营纵切 | 时间 T{snapshot.Tick:00}");
        output.AppendLine();
        output.AppendLine("诊所地图");
        output.AppendLine("[入口] → [接待/分诊] → [全科门诊] → [基础化验] → [治疗室] → [药房] → [出口]");
        output.AppendLine($"接待/分诊：{PatientsAt(snapshot, VerticalPatientStage.Reception, VerticalPatientStage.Triage)}");
        output.AppendLine($"全科门诊：{PatientsAt(snapshot, VerticalPatientStage.Consultation, VerticalPatientStage.FollowUp)}");
        output.AppendLine($"基础化验：采样 {snapshot.SamplingQueue} | 分析 {snapshot.AnalysisQueue} | 结果发布 {snapshot.ResultBacklog}");
        output.AppendLine($"治疗室：{PatientsAt(snapshot, VerticalPatientStage.Treatment)}");
        output.AppendLine($"药房：{PatientsAt(snapshot, VerticalPatientStage.Pharmacy)}");
        output.AppendLine($"出口：{PatientsAt(snapshot, VerticalPatientStage.Discharged)}");
        output.AppendLine();
        output.AppendLine("医院状态");
        output.AppendLine($"已到达患者：{snapshot.ArrivedPatients}");
        output.AppendLine($"在院患者：{snapshot.PatientsInHospital}");
        output.AppendLine($"已离开患者：{snapshot.Summary.DischargedPatients}");
        output.AppendLine($"员工疲劳：{snapshot.StaffFatigue}/100");
        output.AppendLine($"当前瓶颈：{snapshot.CurrentBottleneck}");
        output.AppendLine($"根因链：{snapshot.RootCause}");
        output.AppendLine();
        output.AppendLine("化验室状态");
        output.AppendLine($"采样队列：{snapshot.SamplingQueue}");
        output.AppendLine($"分析队列：{snapshot.AnalysisQueue}");
        output.AppendLine($"结果发布积压：{snapshot.ResultBacklog}");
        output.AppendLine($"临时支援：{(snapshot.TemporarySupportActive ? "已启用" : "未启用")}");
        output.AppendLine($"外送样本：{(snapshot.OutsourcingActive ? "处理中" : "未安排")}");
        output.AppendLine();
        output.AppendLine("患者状态");
        foreach (var patient in snapshot.Patients.Where(patient => patient.Stage != VerticalPatientStage.NotArrived))
        {
            output.AppendLine($"{patient.PatientId}：{StageLabel(patient.Stage)} | 风险：{patient.RiskStatus} | 耐心：{patient.Patience}/100");
        }

        output.AppendLine();
        output.AppendLine("事件轨迹");
        foreach (var entry in events)
        {
            output.AppendLine($"T{entry.Tick:00} | {entry.PatientId} {entry.Event} | 状态：{entry.Status}");
        }

        if (snapshot.ShiftComplete)
        {
            output.AppendLine();
            output.AppendLine("班次结算");
            output.AppendLine($"到达：{snapshot.Summary.ArrivedPatients} | 离开：{snapshot.Summary.DischargedPatients} | 结果发布：{snapshot.Summary.PublishedResults}");
            output.AppendLine($"临时支援：{snapshot.Summary.TemporarySupportUsed} 次 | 外送样本：{snapshot.Summary.OutsourcedSamples} 次 | 结果积压等待：{snapshot.Summary.ResultBacklogWaitTicks} Tick");
            output.AppendLine($"结算结果：{snapshot.Summary.Outcome}");
        }

        return output.ToString();
    }

    private static string PatientsAt(VerticalSliceSnapshot snapshot, params VerticalPatientStage[] stages)
    {
        var patients = snapshot.Patients
            .Where(patient => stages.Contains(patient.Stage))
            .Select(patient => $"{patient.PatientId}（{StageLabel(patient.Stage)}）")
            .ToArray();

        return patients.Length == 0 ? "空" : string.Join("、", patients);
    }

    private static string StageLabel(VerticalPatientStage stage)
    {
        return stage switch
        {
            VerticalPatientStage.NotArrived => "未到达",
            VerticalPatientStage.Reception => "登记",
            VerticalPatientStage.Triage => "分诊",
            VerticalPatientStage.Consultation => "初诊",
            VerticalPatientStage.LabSampling => "等待采样",
            VerticalPatientStage.LabAnalysis => "分析中",
            VerticalPatientStage.ResultPending => "等待结果发布",
            VerticalPatientStage.FollowUp => "结果复诊",
            VerticalPatientStage.Treatment => "治疗中",
            VerticalPatientStage.Pharmacy => "药房等待",
            VerticalPatientStage.Discharged => "已离院",
            _ => "未知"
        };
    }
}
