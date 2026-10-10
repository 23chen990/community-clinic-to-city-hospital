namespace Game.Simulation;

public sealed partial class HospitalSimulation
{
    private readonly Dictionary<long, CommandResult> commandResults = [];
    private readonly HashSet<long> notifiedCriticalIds = [];

    public CommandResult Submit(SimulationCommand command)
    {
        if (seenCommandIds.Contains(command.CommandId))
        {
            // A duplicate is observable to the caller, but must never replace
            // the first command's authoritative result.
            return new CommandResult(
                CommandResultStatus.RejectedDuplicate,
                command.CommandId,
                "command_id 已处理");
        }

        seenCommandIds.Add(command.CommandId);
        if (command.SubmittedTick < Tick)
        {
            var stale = new CommandResult(
                CommandResultStatus.RejectedStale,
                command.CommandId,
                "命令提交时刻已过期");
            // Keep the rejected submission in the authoritative command log.
            // A stale operational command changes the seen/final-result state:
            // resubmitting the same ID must be RejectedDuplicate, and replay
            // hashing must distinguish that state from an unseen command.
            commandLog.Add(command);
            commandResults[command.CommandId] = stale;
            return stale;
        }

        var deferred = new CommandResult(
            CommandResultStatus.Deferred,
            command.CommandId,
            "等待权威 Tick 处理");
        commandResults[command.CommandId] = deferred;
        pendingCommands.Add(command);
        commandLog.Add(command);
        return deferred;
    }

    public CommandResult GetCommandResult(long commandId)
    {
        return commandResults.TryGetValue(commandId, out var result)
            ? result
            : new CommandResult(
                CommandResultStatus.RejectedInvalid,
                commandId,
                "未找到 command_id");
    }

    private void ApplyCommands()
    {
        foreach (var command in pendingCommands
                     .Where(command => command.SubmittedTick <= Tick)
                     .OrderBy(command => command.CommandId)
                     .ToArray())
        {
            pendingCommands.Remove(command);
            var result = ApplyCommand(command);
            commandResults[command.CommandId] = result;
        }
    }

    private CommandResult ApplyCommand(SimulationCommand command)
    {
        switch (command.Type)
        {
            case SimulationCommandType.Pause:
                paused = true;
                return Accepted(command, "暂停已生效");

            case SimulationCommandType.Resume:
                paused = false;
                return Accepted(command, "已恢复运行");

            case SimulationCommandType.SetSpeed:
                if (!Enum.IsDefined(command.Speed))
                {
                    return RejectedInvalid(command, "无效的模拟速度");
                }

                if (speed != command.Speed)
                {
                    speed = command.Speed;
                    AddEvent(
                        SimulationEventType.SpeedChanged,
                        "医院",
                        $"模拟速度调整为 {speed}");
                }

                return Accepted(command, "速度已调整");

            case SimulationCommandType.DispatchLab:
                return ApplyDispatchCommand(command);

            case SimulationCommandType.ArrangeNextDay:
                return ArrangeNextDay(command);

            case SimulationCommandType.ExtendClosing:
                return ExtendClosing(command);

            default:
                AddEvent(SimulationEventType.CommandRejected, "医院", "未知命令类型");
                return RejectedInvalid(command, "未知命令类型");
        }
    }

    private CommandResult ApplyDispatchCommand(SimulationCommand command)
    {
        // Laboratory owns the dispatch preconditions (result backlog,
        // ordinary-sample eligibility, cash, and active-plan conflicts), so
        // preserve its authoritative status and reason verbatim.
        return ApplyDispatch(command);
    }

    private void AutoSlowForNewCriticalPatients()
    {
        foreach (var patient in patients
                     .Where(patient =>
                         patient.Stage != PatientStage.Spawned &&
                         !IsTerminal(patient) &&
                         RiskLevelFor(patient.RiskBasisPoints) == RiskLevel.Critical)
                     .OrderBy(patient => patient.NumericId))
        {
            if (!notifiedCriticalIds.Add(patient.NumericId))
            {
                continue;
            }

            if (speed <= SimulationSpeed.Half)
            {
                continue;
            }

            speed = SimulationSpeed.Half;
            AddEvent(
                SimulationEventType.SpeedChanged,
                "医院",
                "红色危机自动降速至 Half",
                patient.PatientId);
        }
    }

    private static CommandResult Accepted(SimulationCommand command, string reason) =>
        new(CommandResultStatus.Accepted, command.CommandId, reason);

    private static CommandResult RejectedInvalid(SimulationCommand command, string reason) =>
        new(CommandResultStatus.RejectedInvalid, command.CommandId, reason);

    private static CommandResult RejectedInsufficient(SimulationCommand command, string reason) =>
        new(CommandResultStatus.RejectedInsufficient, command.CommandId, reason);
}
