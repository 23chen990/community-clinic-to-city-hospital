using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Game.Simulation;

public sealed partial class HospitalSimulation
{
    private string ComputeStateHash()
    {
        var builder = new StringBuilder();
        AppendField(builder, "format");
        AppendString(builder, "m1-authoritative-state-v2");

        AppendConfigCanonicalState(builder);

        // These are authoritative Tick and scheduling cursors. Paused and
        // speed are presentation/frame controls and intentionally omitted.
        AppendField(builder, "tick");
        AppendInt(builder, Tick);
        AppendField(builder, "dispatchPlan");
        AppendEnum(builder, dispatchPlan);
        AppendField(builder, "supportUntilTick");
        AppendInt(builder, supportUntilTick);
        AppendField(builder, "nextResultPublishTick");
        AppendInt(builder, nextResultPublishTick);
        AppendField(builder, "nextTaskId");
        AppendLong(builder, nextTaskId);
        AppendField(builder, "technicianFatigueBasisPoints");
        AppendInt(builder, technicianFatigueBasisPoints);
        AppendField(builder, "rngState");
        AppendLong(builder, random.State);

        AppendPatientsCanonicalState(builder);
        AppendQueuesCanonicalState(builder);
        AppendTasksCanonicalState(builder);
        AppendResourcesCanonicalState(builder);
        AppendResultQueuesCanonicalState(builder);
        AppendLedgerCanonicalState(builder);
        AppendEventsCanonicalState(builder);
        AppendRootCauseCanonicalState(builder);
        AppendOperationalCommandCanonicalState(builder);

        // The shift partial owns its authoritative state. The method is
        // supplied by M1Simulation.Shifts.cs and is intentionally kept out of
        // this file.
        AppendShiftCanonicalState(builder);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private void AppendConfigCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "config");
        var properties = typeof(M1SimulationConfig)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod is not null)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        AppendCount(builder, properties.Length);

        foreach (var property in properties)
        {
            AppendString(builder, property.Name);
            var declaredType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var value = property.GetValue(config);
            AppendConfigValue(builder, declaredType, value, property.Name);
        }
    }

    private static void AppendConfigValue(
        StringBuilder builder,
        Type declaredType,
        object? value,
        string propertyName)
    {
        if (declaredType == typeof(int))
        {
            builder.Append('I');
            if (value is null)
            {
                AppendNull(builder);
            }
            else
            {
                AppendInt(builder, (int)value);
            }

            return;
        }

        if (declaredType == typeof(long))
        {
            builder.Append('L');
            if (value is null)
            {
                AppendNull(builder);
            }
            else
            {
                AppendLong(builder, (long)value);
            }

            return;
        }

        if (declaredType == typeof(bool))
        {
            builder.Append('B');
            if (value is null)
            {
                AppendNull(builder);
            }
            else
            {
                AppendBool(builder, (bool)value);
            }

            return;
        }

        if (declaredType == typeof(string))
        {
            builder.Append('S');
            AppendString(builder, (string?)value);
            return;
        }

        throw new InvalidOperationException(
            $"M1SimulationConfig property '{propertyName}' has unsupported canonical type '{declaredType.FullName}'.");
    }

    private void AppendPatientsCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "patients");
        var orderedPatients = patients.OrderBy(patient => patient.NumericId).ToArray();
        AppendCount(builder, orderedPatients.Length);

        foreach (var patient in orderedPatients)
        {
            AppendLong(builder, patient.NumericId);
            AppendString(builder, patient.PatientId);
            AppendString(builder, patient.CaseId);
            AppendInt(builder, patient.ArrivalTick);
            AppendBool(builder, patient.RequiresLab);
            AppendBool(builder, patient.RequiresTreatment);
            AppendInt(builder, patient.RiskBasisPoints);
            AppendInt(builder, patient.PatienceBasisPoints);
            AppendInt(builder, patient.WaitTicks);
            AppendInt(builder, patient.TotalWaitingTicks);
            AppendInt(builder, patient.StageEnteredTick);
            AppendInt(builder, patient.OutsourceReturnTick);
            AppendLong(builder, patient.RevenueMinorUnits);
            AppendEnum(builder, patient.Stage);
            AppendBool(builder, patient.LabEvidenceApplied);

            var diagnosis = patient.Diagnosis;
            AppendInt(builder, diagnosis.ConfidenceBasisPoints);
            AppendString(builder, diagnosis.TopCandidateId);
            var supports = diagnosis.Supports
                .OrderBy(support => support.CandidateId, StringComparer.Ordinal)
                .ToArray();
            AppendCount(builder, supports.Length);
            foreach (var support in supports)
            {
                AppendString(builder, support.CandidateId);
                AppendInt(builder, support.SupportBasisPoints);
            }
        }
    }

    private void AppendQueuesCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "queues");
        AppendCount(builder, ServiceOrder.Length);
        foreach (var service in ServiceOrder)
        {
            AppendEnum(builder, service);
            var queue = queues[service]
                .OrderBy(patient => patient.StageEnteredTick)
                .ThenBy(patient => patient.NumericId)
                .ToArray();
            AppendCount(builder, queue.Length);
            foreach (var patient in queue)
            {
                AppendLong(builder, patient.NumericId);
            }
        }
    }

    private void AppendTasksCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "activeTasks");
        var tasks = activeTasks.Values
            .OrderBy(task => task.TaskId)
            .ThenBy(task => task.Service)
            .ToArray();
        AppendCount(builder, tasks.Length);
        foreach (var task in tasks)
        {
            AppendLong(builder, task.TaskId);
            AppendEnum(builder, task.Service);
            AppendLong(builder, task.Patient.NumericId);
            AppendInt(builder, task.EndTick);
            var resourcesForTask = task.Resources
                .OrderBy(resource => resource, StringComparer.Ordinal)
                .ToArray();
            AppendCount(builder, resourcesForTask.Length);
            foreach (var resource in resourcesForTask)
            {
                AppendString(builder, resource);
            }
        }
    }

    private void AppendResourcesCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "resources");
        var resourcesInCanonicalOrder = resources.CanonicalState()
            .OrderBy(resource => resource.Id, StringComparer.Ordinal)
            .ToArray();
        AppendCount(builder, resourcesInCanonicalOrder.Length);
        foreach (var resource in resourcesInCanonicalOrder)
        {
            AppendString(builder, resource.Id);
            AppendInt(builder, resource.Used);
            AppendInt(builder, resource.Capacity);
        }
    }

    private void AppendResultQueuesCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "resultQueue");
        var results = resultQueue.OrderBy(patient => patient.NumericId).ToArray();
        AppendCount(builder, results.Length);
        foreach (var patient in results)
        {
            AppendLong(builder, patient.NumericId);
        }

        AppendField(builder, "outsourcedQueue");
        var outsourced = outsourcedQueue.OrderBy(patient => patient.NumericId).ToArray();
        AppendCount(builder, outsourced.Length);
        foreach (var patient in outsourced)
        {
            AppendLong(builder, patient.NumericId);
        }
    }

    private void AppendLedgerCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "ledger");
        var entries = ledgerEntries
            .OrderBy(entry => entry.IdempotencyKey, StringComparer.Ordinal)
            .ThenBy(entry => entry.Tick)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.EntityId, StringComparer.Ordinal)
            .ToArray();
        AppendCount(builder, entries.Length);
        foreach (var entry in entries)
        {
            AppendString(builder, entry.IdempotencyKey);
            AppendEnum(builder, entry.Kind);
            AppendLong(builder, entry.AmountMinorUnits);
            AppendInt(builder, entry.Tick);
            AppendString(builder, entry.EntityId);
        }
    }

    private void AppendEventsCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "events");
        // SpeedChanged is a presentation/frame-control event. It is omitted
        // so pause/speed paths remain equal under SIM002.
        var authoritativeEvents = events
            .Where(entry => entry.EventType != SimulationEventType.SpeedChanged)
            .ToArray();
        AppendCount(builder, authoritativeEvents.Length);
        foreach (var entry in authoritativeEvents)
        {
            AppendInt(builder, entry.Tick);
            AppendEnum(builder, entry.EventType);
            AppendString(builder, entry.PatientId);
            AppendString(builder, entry.Status);
            AppendString(builder, entry.Detail);
        }
    }

    private void AppendRootCauseCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "rootCauseEdges");
        var edges = rootCauseEdges
            .OrderBy(edge => edge.StartTick)
            .ThenBy(edge => edge.EndTick)
            .ThenBy(edge => edge.CauseEntity, StringComparer.Ordinal)
            .ThenBy(edge => edge.EffectEntity, StringComparer.Ordinal)
            .ThenBy(edge => edge.ReasonCode)
            .ThenBy(edge => edge.Magnitude)
            .ThenBy(edge => edge.ParentEdgeId, StringComparer.Ordinal)
            .ToArray();
        AppendCount(builder, edges.Length);
        foreach (var edge in edges)
        {
            AppendString(builder, edge.CauseEntity);
            AppendString(builder, edge.EffectEntity);
            AppendEnum(builder, edge.ReasonCode);
            AppendInt(builder, edge.StartTick);
            AppendInt(builder, edge.EndTick);
            AppendInt(builder, edge.Magnitude);
            AppendString(builder, edge.ParentEdgeId);
        }
    }

    private void AppendOperationalCommandCanonicalState(StringBuilder builder)
    {
        AppendField(builder, "operationalCommands");
        var commands = commandLog
            .Where(command => IsOperationalCommand(command.Type))
            .OrderBy(command => command.CommandId)
            .ThenBy(command => command.SubmittedTick)
            .ToArray();
        AppendCount(builder, commands.Length);
        foreach (var command in commands)
        {
            AppendCommand(builder, command);
        }

        // commandLog contains each first-seen submission, including stale
        // rejections. The intersection below records the
        // authoritative seen set without allowing display controls to affect
        // the replay hash.
        AppendField(builder, "operationalSeenCommandIds");
        var operationalSeenIds = commands
            .Select(command => command.CommandId)
            .Where(seenCommandIds.Contains)
            .OrderBy(commandId => commandId)
            .ToArray();
        AppendCount(builder, operationalSeenIds.Length);
        foreach (var commandId in operationalSeenIds)
        {
            AppendLong(builder, commandId);
        }

        AppendField(builder, "operationalPendingCommands");
        var pendingCommandsInCanonicalOrder = pendingCommands
            .Where(command => IsOperationalCommand(command.Type))
            .OrderBy(command => command.SubmittedTick)
            .ThenBy(command => command.CommandId)
            .ToArray();
        AppendCount(builder, pendingCommandsInCanonicalOrder.Length);
        foreach (var command in pendingCommandsInCanonicalOrder)
        {
            AppendCommand(builder, command);
        }

        AppendField(builder, "operationalCommandResults");
        var resultCommands = commands
            .Where(command => commandResults.ContainsKey(command.CommandId))
            .ToArray();
        AppendCount(builder, resultCommands.Length);
        foreach (var command in resultCommands)
        {
            var result = commandResults[command.CommandId];
            AppendLong(builder, result.CommandId);
            AppendEnum(builder, result.Status);
            AppendString(builder, result.Reason);
        }
    }

    private static void AppendCommand(StringBuilder builder, SimulationCommand command)
    {
        AppendLong(builder, command.CommandId);
        AppendInt(builder, command.SubmittedTick);
        AppendEnum(builder, command.Type);
        AppendEnum(builder, command.DispatchPlan);
        AppendEnum(builder, command.Speed);
    }

    private static bool IsOperationalCommand(SimulationCommandType type) => type is not
        (SimulationCommandType.Pause or SimulationCommandType.Resume or SimulationCommandType.SetSpeed);

    private static void AppendField(StringBuilder builder, string fieldName)
    {
        AppendString(builder, fieldName);
    }

    private static void AppendCount(StringBuilder builder, int count)
    {
        AppendInt(builder, count);
    }

    private static void AppendEnum<TEnum>(StringBuilder builder, TEnum value)
        where TEnum : struct, Enum
    {
        AppendInt(builder, Convert.ToInt32(value));
    }

    private static void AppendInt(StringBuilder builder, int value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
    }

    private static void AppendLong(StringBuilder builder, long value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
    }

    private static void AppendBool(StringBuilder builder, bool value)
    {
        builder.Append(value ? '1' : '0').Append(';');
    }

    private static void AppendNull(StringBuilder builder)
    {
        builder.Append('n').Append(';');
    }

    private static void AppendString(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        builder.Append(byteCount.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }
}
