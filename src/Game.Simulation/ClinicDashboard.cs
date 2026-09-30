using System.Text;

namespace Game.Simulation;

public static class ClinicDashboard
{
    public static string Render(ClinicSnapshot snapshot, IReadOnlyList<ClinicEvent> events)
    {
        var output = new StringBuilder();
        output.AppendLine($"M1-A 最小医院经营沙盒 | 时间 T{snapshot.Tick:00}");
        output.AppendLine();
        output.AppendLine("诊所布局");
        output.AppendLine("[入口] → [接待区] → [候诊区] → [医生诊室] → [出口]");
        output.AppendLine($"入口：{ZoneStatus(snapshot, PatientLocation.Entrance)}");
        output.AppendLine($"接待区：{ReceptionStatus(snapshot)}");
        output.AppendLine($"候诊区：{DoctorQueueStatus(snapshot)}");
        output.AppendLine($"医生诊室：{DoctorRoomStatus(snapshot)}");
        output.AppendLine($"出口：{ZoneStatus(snapshot, PatientLocation.Exit)}");
        output.AppendLine();
        output.AppendLine("医院状态");
        output.AppendLine($"在院患者：{snapshot.PatientsInClinic}");
        output.AppendLine($"等待患者：{snapshot.WaitingPatients}");
        output.AppendLine($"接待状态：{(snapshot.ReceptionBusy ? "忙碌" : "空闲")}");
        output.AppendLine($"医生状态：{(snapshot.DoctorBusy ? "忙碌" : "空闲")}");
        output.AppendLine($"已完成诊疗：{snapshot.CompletedVisits}");
        output.AppendLine($"已离开患者：{snapshot.DepartedPatients}");
        output.AppendLine($"当前瓶颈：{snapshot.CurrentBottleneck}");
        output.AppendLine();
        output.AppendLine("事件轨迹");

        var multiplePatients = events.Select(entry => entry.PatientId).Distinct().Count() > 1;
        foreach (var entry in events)
        {
            var patientLabel = multiplePatients ? $"{entry.PatientId} " : string.Empty;
            var eventLabel = multiplePatients
                ? entry.Event switch
                {
                    "患者进入" => "进入医院",
                    "患者开始等待" => "开始等待",
                    "患者离开" => "离开医院",
                    _ => entry.Event
                }
                : entry.Event;
            output.AppendLine($"T{entry.Tick:00} | {patientLabel}{eventLabel} | 状态：{entry.Status}");
        }

        return output.ToString();
    }

    private static string ReceptionStatus(ClinicSnapshot snapshot)
    {
        var patients = snapshot.Patients
            .Where(patient => patient.Location is PatientLocation.ReceptionQueue or PatientLocation.InReception)
            .Select(patient => patient.Location == PatientLocation.InReception
                ? $"{patient.PatientId}（正在登记）"
                : $"{patient.PatientId}（等待接待 {patient.WaitingTicks} Tick）")
            .ToArray();

        return patients.Length == 0 ? "空" : string.Join("、", patients);
    }

    private static string DoctorQueueStatus(ClinicSnapshot snapshot)
    {
        var patients = snapshot.Patients
            .Where(patient => patient.Location == PatientLocation.DoctorQueue)
            .Select(patient => $"{patient.PatientId}（等待医生 {patient.WaitingTicks} Tick）")
            .ToArray();

        return patients.Length == 0 ? "空" : string.Join("、", patients);
    }

    private static string DoctorRoomStatus(ClinicSnapshot snapshot)
    {
        var patients = snapshot.Patients
            .Where(patient => patient.Location is PatientLocation.InDoctorRoom or PatientLocation.VisitCompleted)
            .Select(patient => patient.Location == PatientLocation.InDoctorRoom
                ? $"{patient.PatientId}（正在接诊）"
                : $"{patient.PatientId}（诊疗完成，等待离开）")
            .ToArray();

        return patients.Length == 0 ? "空" : string.Join("、", patients);
    }

    private static string ZoneStatus(ClinicSnapshot snapshot, PatientLocation location)
    {
        var patients = snapshot.Patients
            .Where(patient => patient.Location == location)
            .Select(patient => location switch
            {
                PatientLocation.Entrance => $"{patient.PatientId}（刚刚进入）",
                PatientLocation.Exit => $"{patient.PatientId}（已离开）",
                _ => patient.PatientId
            })
            .ToArray();

        return patients.Length == 0 ? "空" : string.Join("、", patients);
    }
}
