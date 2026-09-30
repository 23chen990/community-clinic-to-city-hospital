namespace Game.Simulation;

public enum PatientLocation
{
    Entrance,
    ReceptionQueue,
    InReception,
    DoctorQueue,
    InDoctorRoom,
    VisitCompleted,
    Exit
}

public sealed record ClinicEvent(int Tick, string Event, string Status, string PatientId = "patient-001");

public sealed record ClinicPatientView(string PatientId, PatientLocation Location, int WaitingTicks);

public sealed record ClinicSnapshot(
    int Tick,
    PatientLocation PatientLocation,
    int PatientWaitingTicks,
    int PatientsInClinic,
    int WaitingPatients,
    bool ReceptionBusy,
    bool DoctorBusy,
    int CompletedVisits,
    int DepartedPatients,
    string CurrentBottleneck,
    IReadOnlyList<ClinicPatientView> Patients)
{
    public bool IsFinished => PatientsInClinic == 0;
}

public sealed class ClinicSandbox
{
    private readonly List<PatientState> patients;
    private readonly List<ClinicEvent> events;
    private int tick;

    public ClinicSandbox()
        : this(includeSecondPatient: false)
    {
    }

    private ClinicSandbox(bool includeSecondPatient)
    {
        patients =
        [
            new PatientState("患者001", arrivalOrder: 0),
        ];

        if (includeSecondPatient)
        {
            patients.Add(new PatientState("患者002", arrivalOrder: 1));
        }

        events = [];
        foreach (var patient in patients)
        {
            AddEvent(patient, "患者进入", "已到达医院");
        }
    }

    public static ClinicSandbox CreateQueueScenario() => new(includeSecondPatient: true);

    public ClinicSnapshot Snapshot
    {
        get
        {
            var primaryPatient = patients[0];
            var patientViews = patients
                .Select(patient => new ClinicPatientView(patient.PatientId, patient.Location, patient.WaitingTicks))
                .ToArray();

            return new ClinicSnapshot(
                tick,
                primaryPatient.Location,
                primaryPatient.WaitingTicks,
                patients.Count(patient => patient.Location != PatientLocation.Exit),
                patients.Count(patient => patient.Location is PatientLocation.ReceptionQueue or PatientLocation.DoctorQueue),
                patients.Any(patient => patient.Location == PatientLocation.InReception),
                patients.Any(patient => patient.Location == PatientLocation.InDoctorRoom),
                patients.Count(patient => patient.Location is PatientLocation.VisitCompleted or PatientLocation.Exit),
                patients.Count(patient => patient.Location == PatientLocation.Exit),
                patients.Any(patient => patient.Location == PatientLocation.ReceptionQueue)
                    ? "接待等待"
                    : patients.Any(patient => patient.Location == PatientLocation.DoctorQueue)
                        ? "医生等待"
                        : "无",
                patientViews);
        }
    }

    public IReadOnlyList<ClinicEvent> Events => events.ToArray();

    public void AdvanceTime()
    {
        tick++;

        foreach (var patient in patients.OrderBy(patient => patient.ArrivalOrder))
        {
            switch (patient.Location)
            {
                case PatientLocation.Entrance:
                    patient.Location = PatientLocation.ReceptionQueue;
                    patient.WaitingTicks = 0;
                    AddEvent(patient, "患者开始等待", "等待接待");
                    break;
                case PatientLocation.ReceptionQueue:
                case PatientLocation.DoctorQueue:
                    patient.WaitingTicks++;
                    break;
                case PatientLocation.InReception:
                    patient.Location = PatientLocation.DoctorQueue;
                    patient.WaitingTicks = 0;
                    AddEvent(patient, "登记完成", "登记完成");
                    AddEvent(patient, "患者开始等待", "等待医生");
                    break;
                case PatientLocation.InDoctorRoom:
                    patient.Location = PatientLocation.VisitCompleted;
                    AddEvent(patient, "诊疗完成", "完成一次诊疗");
                    break;
                case PatientLocation.VisitCompleted:
                    patient.Location = PatientLocation.Exit;
                    AddEvent(patient, "患者离开", "已离开医院");
                    break;
                case PatientLocation.Exit:
                    break;
                default:
                    throw new InvalidOperationException("未知患者位置。");
            }
        }
    }

    public bool StartReception()
    {
        var patient = patients
            .Where(patient => patient.Location == PatientLocation.ReceptionQueue)
            .OrderBy(patient => patient.ArrivalOrder)
            .FirstOrDefault();

        if (patient is null)
        {
            return false;
        }

        patient.Location = PatientLocation.InReception;
        patient.WaitingTicks = 0;
        AddEvent(patient, "接待开始", "正在登记");
        return true;
    }

    public bool StartDoctorVisit()
    {
        var patient = patients
            .Where(patient => patient.Location == PatientLocation.DoctorQueue)
            .OrderBy(patient => patient.ArrivalOrder)
            .FirstOrDefault();

        if (patient is null)
        {
            return false;
        }

        patient.Location = PatientLocation.InDoctorRoom;
        patient.WaitingTicks = 0;
        AddEvent(patient, "接诊开始", "医生正在接诊");
        return true;
    }

    private void AddEvent(PatientState patient, string eventName, string status)
    {
        events.Add(new ClinicEvent(tick, eventName, status, patient.PatientId));
    }

    private sealed class PatientState
    {
        public PatientState(string patientId, int arrivalOrder)
        {
            PatientId = patientId;
            ArrivalOrder = arrivalOrder;
            Location = PatientLocation.Entrance;
        }

        public string PatientId { get; }
        public int ArrivalOrder { get; }
        public PatientLocation Location { get; set; }
        public int WaitingTicks { get; set; }
    }
}
