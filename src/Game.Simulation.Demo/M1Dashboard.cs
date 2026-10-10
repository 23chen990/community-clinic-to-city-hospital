using System.Text;
using Game.Simulation;

namespace Game.Simulation.Demo;

public static class M1Dashboard
{
    public static string Render(HospitalSnapshot snapshot, IReadOnlyList<SimulationEvent> events)
    {
        var output = new StringBuilder();
        output.AppendLine($"M1 经营纵切 | 第{snapshot.Day}日 白班 | 时间 T{snapshot.ShiftTick:0000}");
        output.AppendLine();
        output.AppendLine("诊所地图");
        output.AppendLine("[入口] → [接待与分诊] → [全科门诊] → [基础化验室] → [治疗室] → [药房] → [出口]");
        output.AppendLine($"接待与分诊：{CountAt(snapshot, PatientStage.WaitingReception, PatientStage.InReception, PatientStage.WaitingTriage, PatientStage.InTriage)}");
        output.AppendLine($"全科门诊：{CountAt(snapshot, PatientStage.WaitingConsult, PatientStage.InConsult, PatientStage.WaitingFollowUp, PatientStage.InFollowUp)}");
        output.AppendLine($"基础化验室：采样队列 {snapshot.Lab.SamplingQueue} 人 | 分析队列 {snapshot.Lab.AnalysisQueue} 份 | 结果发布积压：{snapshot.Lab.ResultBacklog} 份");
        output.AppendLine($"治疗室：{CountAt(snapshot, PatientStage.WaitingTreatment, PatientStage.InTreatment)}");
        output.AppendLine($"药房：{CountAt(snapshot, PatientStage.WaitingPharmacy, PatientStage.InPharmacy)}");
        output.AppendLine();
        output.AppendLine("排队患者");
        var waitingPatients = snapshot.Patients
            .Where(patient => patient.Stage is PatientStage.ResultPending or PatientStage.Outsourced or
                PatientStage.WaitingReception or PatientStage.WaitingTriage or PatientStage.WaitingConsult or
                PatientStage.WaitingFollowUp or PatientStage.WaitingTreatment or PatientStage.WaitingPharmacy)
            .OrderByDescending(patient => patient.RiskBasisPoints)
            .ThenBy(patient => patient.PatientId, StringComparer.Ordinal)
            .ToArray();
        if (waitingPatients.Length == 0)
        {
            output.AppendLine("空");
        }
        else
        {
            foreach (var patient in waitingPatients)
            {
                output.AppendLine($"{patient.PatientId} | {StageLabel(patient.Stage)} | 风险 {patient.RiskLevel} | 等待 {patient.WaitTicks} Tick");
            }
        }

        output.AppendLine();
        output.AppendLine("化验调度");
        output.AppendLine($"当前方案：{DispatchLabel(snapshot.Lab.DispatchPlan)} | 技师疲劳：{snapshot.Lab.TechnicianFatigueBasisPoints / 100}%");
        output.AppendLine($"当前瓶颈：{snapshot.Lab.CurrentBottleneck}");
        output.AppendLine();
        output.AppendLine("医院状态");
        output.AppendLine($"接诊：{snapshot.Summary.ArrivedPatients} | 出院：{snapshot.Summary.DischargedPatients} | 转院：{snapshot.Summary.TransferredPatients} | 治疗中：{snapshot.Summary.TreatmentInProgress}");
        output.AppendLine($"员工状态：化验技师疲劳 {snapshot.Lab.TechnicianFatigueBasisPoints / 100}%");
        output.AppendLine($"本班收入：{snapshot.Summary.RevenueMinorUnits}");
        output.AppendLine($"外送成本：{snapshot.Summary.DispatchCostMinorUnits} | 当前现金：{snapshot.CashMinorUnits}");
        output.AppendLine($"未完成诊疗离院：{snapshot.Summary.UnfinishedPatients} | 再次到院：{snapshot.Summary.ReturningPatients}");
        output.AppendLine();
        output.AppendLine("事件轨迹");
        foreach (var entry in events.TakeLast(24))
        {
            output.AppendLine($"T{entry.Tick:0000} | {entry.PatientId} | {entry.Status}");
        }

        if (snapshot.ShiftComplete)
        {
            output.AppendLine();
            output.AppendLine("班次结算");
            output.AppendLine($"医院状态：接诊 {snapshot.Summary.ArrivedPatients}，出院 {snapshot.Summary.DischargedPatients}，转院 {snapshot.Summary.TransferredPatients}");
            foreach (var department in snapshot.Summary.DepartmentStatuses)
            {
                output.AppendLine($"{department.Key}：{department.Value}");
            }

            output.AppendLine($"本班收入：{snapshot.Summary.RevenueMinorUnits}");
            output.AppendLine($"结算结果：{snapshot.Summary.Outcome}");
        }

        return output.ToString();
    }

    private static string CountAt(HospitalSnapshot snapshot, params PatientStage[] stages)
    {
        var count = snapshot.Patients.Count(patient => stages.Contains(patient.Stage));
        return count == 0 ? "空" : $"{count} 人";
    }

    private static string StageLabel(PatientStage stage) => stage switch
    {
        PatientStage.WaitingReception or PatientStage.InReception => "接待",
        PatientStage.WaitingTriage or PatientStage.InTriage => "分诊",
        PatientStage.WaitingConsult or PatientStage.InConsult => "全科门诊",
        PatientStage.WaitingLabSampling or PatientStage.InLabSampling => "等待采样",
        PatientStage.WaitingLabAnalysis or PatientStage.InLabAnalysis => "等待分析",
        PatientStage.ResultPending => "等待结果发布",
        PatientStage.Outsourced => "外送处理中",
        PatientStage.WaitingFollowUp or PatientStage.InFollowUp => "结果复诊",
        PatientStage.WaitingTreatment or PatientStage.InTreatment => "治疗",
        PatientStage.WaitingPharmacy or PatientStage.InPharmacy => "药房",
        _ => stage.ToString(),
    };

    private static string DispatchLabel(LabDispatchPlan plan) => plan switch
    {
        LabDispatchPlan.RiskPriority => "风险优先",
        LabDispatchPlan.TemporarySupport => "临时支援",
        LabDispatchPlan.OutsourceSamples => "外送样本",
        _ => "未安排",
    };
}
