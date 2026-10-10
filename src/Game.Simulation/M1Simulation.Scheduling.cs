namespace Game.Simulation;

public sealed partial class HospitalSimulation
{
    private static readonly ServiceKind[] ServiceOrder =
    [
        ServiceKind.Reception,
        ServiceKind.Triage,
        ServiceKind.InitialConsultation,
        ServiceKind.LabSampling,
        ServiceKind.LabAnalysis,
        ServiceKind.FollowUp,
        ServiceKind.Treatment,
        ServiceKind.Pharmacy,
    ];

    private void ScheduleServices()
    {
        foreach (var service in ServiceOrder)
        {
            if (service is ServiceKind.InitialConsultation or ServiceKind.FollowUp)
            {
                continue;
            }

            if (activeTasks.ContainsKey(service) || queues[service].Count == 0)
            {
                continue;
            }

            var patient = DequeueNext(service);
            var resourceIds = ResourcesFor(service);
            if (!resources.TryReserve(resourceIds))
            {
                queues[service].Insert(0, patient);
                continue;
            }

            StartService(service, patient, resourceIds);
        }

        ScheduleConsultation();
    }

    private void ScheduleConsultation()
    {
        if (activeTasks.ContainsKey(ServiceKind.InitialConsultation) ||
            activeTasks.ContainsKey(ServiceKind.FollowUp))
        {
            return;
        }

        var service = SelectConsultationService();
        if (service is null)
        {
            return;
        }

        var patient = DequeueNext(service.Value);
        var resourceIds = ResourcesFor(service.Value);
        if (!resources.TryReserve(resourceIds))
        {
            queues[service.Value].Insert(0, patient);
            return;
        }

        StartService(service.Value, patient, resourceIds);
    }

    private ServiceKind? SelectConsultationService()
    {
        var initialQueue = queues[ServiceKind.InitialConsultation];
        var followUpQueue = queues[ServiceKind.FollowUp];
        if (initialQueue.Count == 0 && followUpQueue.Count == 0)
        {
            return null;
        }

        var highRiskFollowUp = followUpQueue
            .Where(patient => RiskLevelFor(patient.RiskBasisPoints) >= RiskLevel.High)
            .OrderBy(patient => patient.StageEnteredTick)
            .ThenBy(patient => patient.NumericId)
            .FirstOrDefault();
        if (highRiskFollowUp is not null)
        {
            return ServiceKind.FollowUp;
        }

        if (initialQueue.Count == 0)
        {
            return ServiceKind.FollowUp;
        }

        if (followUpQueue.Count == 0)
        {
            return ServiceKind.InitialConsultation;
        }

        var initialSinceFollowUp = InitialConsultationsSinceLastFollowUp();
        return initialSinceFollowUp < 2
            ? ServiceKind.InitialConsultation
            : ServiceKind.FollowUp;
    }

    private int InitialConsultationsSinceLastFollowUp()
    {
        var count = 0;
        for (var index = events.Count - 1; index >= 0; index--)
        {
            var eventType = events[index].EventType;
            if (eventType == SimulationEventType.FollowUpStarted)
            {
                break;
            }

            if (eventType == SimulationEventType.ConsultationStarted)
            {
                count++;
            }
        }

        return count;
    }

    private void StartService(ServiceKind service, PatientState patient, IReadOnlyList<string> resourceIds)
    {
        patient.WaitTicks = 0;
        patient.Stage = StageForStart(service);
        var duration = service switch
        {
            ServiceKind.Treatment => config.TreatmentServiceTicks,
            ServiceKind.LabAnalysis => config.LabAnalysisServiceTicks,
            _ => config.ServiceTicks,
        };
        if (service == ServiceKind.LabAnalysis)
        {
            duration = ApplyFatigueDuration(duration);
        }
        duration = ResumeDurationOrDefault(patient, duration);
        patient.StageEnteredTick = Tick;
        activeTasks[service] = new ActiveTask(nextTaskId++, service, patient, Tick + duration, resourceIds);

        var eventType = service switch
        {
            ServiceKind.Reception => SimulationEventType.RegistrationStarted,
            ServiceKind.Triage => SimulationEventType.TriageStarted,
            ServiceKind.InitialConsultation or ServiceKind.FollowUp =>
                service == ServiceKind.FollowUp
                    ? SimulationEventType.FollowUpStarted
                    : SimulationEventType.ConsultationStarted,
            ServiceKind.LabSampling => SimulationEventType.LabSamplingStarted,
            ServiceKind.LabAnalysis => SimulationEventType.LabAnalysisStarted,
            ServiceKind.Treatment => SimulationEventType.TreatmentStarted,
            ServiceKind.Pharmacy => SimulationEventType.PharmacyStarted,
            _ => throw new InvalidOperationException($"未处理的服务类型：{service}"),
        };
        AddEvent(eventType, patient.PatientId, eventType.ToString());
    }

    private PatientState DequeueNext(ServiceKind service)
    {
        var queue = queues[service];
        var patient = service == ServiceKind.FollowUp
            ? queue
                .OrderByDescending(candidate => RiskLevelFor(candidate.RiskBasisPoints) >= RiskLevel.High)
                .ThenByDescending(candidate => dispatchPlan == LabDispatchPlan.RiskPriority
                    ? candidate.RiskBasisPoints
                    : 0)
                .ThenBy(candidate => candidate.StageEnteredTick)
                .ThenBy(candidate => candidate.NumericId)
                .First()
            : queue
                .OrderBy(candidate => candidate.StageEnteredTick)
                .ThenBy(candidate => candidate.NumericId)
                .First();
        queue.Remove(patient);
        return patient;
    }

    private void Enqueue(ServiceKind service, PatientState patient)
    {
        if (!queues[service].Contains(patient))
        {
            patient.StageEnteredTick = Tick;
            queues[service].Add(patient);
        }
    }

    private static PatientStage StageForStart(ServiceKind service) => service switch
    {
        ServiceKind.Reception => PatientStage.InReception,
        ServiceKind.Triage => PatientStage.InTriage,
        ServiceKind.InitialConsultation => PatientStage.InConsult,
        ServiceKind.FollowUp => PatientStage.InFollowUp,
        ServiceKind.LabSampling => PatientStage.InLabSampling,
        ServiceKind.LabAnalysis => PatientStage.InLabAnalysis,
        ServiceKind.Treatment => PatientStage.InTreatment,
        ServiceKind.Pharmacy => PatientStage.InPharmacy,
        _ => throw new InvalidOperationException($"未处理的服务类型：{service}"),
    };

    private static IReadOnlyList<string> ResourcesFor(ServiceKind service) => service switch
    {
        ServiceKind.Reception => ["Receptionist"],
        ServiceKind.Triage => ["TriageNurse"],
        ServiceKind.InitialConsultation or ServiceKind.FollowUp => ["GeneralPractitioner"],
        ServiceKind.LabSampling => ["SamplingStation", "SamplingNurse"],
        ServiceKind.LabAnalysis => ["Analyzer", "LabTechnician"],
        ServiceKind.Treatment => ["TreatmentNurse"],
        ServiceKind.Pharmacy => ["Pharmacist"],
        _ => throw new InvalidOperationException($"未处理的服务类型：{service}"),
    };
}
