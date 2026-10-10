using Game.Simulation;

namespace Game.Simulation.Demo;

public static class M1SimulationSession
{
    public static void Run(TextReader input, TextWriter output, HospitalSimulation? simulation = null)
    {
        simulation ??= HospitalSimulation.CreateBaseline(seed: 7);
        long nextCommandId = 1;

        output.WriteLine("M1 纯 C# 经营纵切");
        output.WriteLine("观察接待、分诊、全科、化验三段队列，并处理结果发布积压。\n");

        while (true)
        {
            output.WriteLine(M1Dashboard.Render(simulation.Snapshot, simulation.Events));

            if (simulation.Snapshot.ShiftComplete)
            {
                output.WriteLine("[D] 安排下一日  [Q] 退出");
            }
            else
            {
                output.WriteLine("可用操作：[A] 推进到下一个关键状态  [N] 推进 1 Tick  [R] 风险优先  [S] 临时支援  [O] 外送样本  [E] 延长收尾一次  [P] 暂停/继续  [Q] 退出");
            }
            output.Write("请输入操作：");
            var command = input.ReadLine()?.Trim().ToLowerInvariant();
            if (command is null or "q")
            {
                output.WriteLine("已退出纵切。");
                return;
            }

            switch (command)
            {
                case "n":
                    simulation.AdvanceTick();
                    break;
                case "a":
                    AdvanceToDecision(simulation);
                    break;
                case "r":
                    Dispatch(simulation, ref nextCommandId, LabDispatchPlan.RiskPriority, output);
                    break;
                case "s":
                    Dispatch(simulation, ref nextCommandId, LabDispatchPlan.TemporarySupport, output);
                    break;
                case "o":
                    Dispatch(simulation, ref nextCommandId, LabDispatchPlan.OutsourceSamples, output);
                    break;
                case "d":
                    Execute(simulation, new SimulationCommand(nextCommandId++, simulation.Tick, SimulationCommandType.ArrangeNextDay), output);
                    break;
                case "e":
                    Execute(simulation, new SimulationCommand(nextCommandId++, simulation.Tick, SimulationCommandType.ExtendClosing), output);
                    break;
                case "p":
                    TogglePause(simulation, ref nextCommandId);
                    break;
                default:
                    output.WriteLine("操作未执行：请观察地图和当前瓶颈。");
                    break;
            }
        }
    }

    private static void AdvanceToDecision(HospitalSimulation simulation)
    {
        var current = simulation.Snapshot;
        var alreadyAtDecision = current.Phase == ShiftPhase.Operating &&
            (current.Lab.ResultBacklog > 0 ||
            current.Patients.Any(patient =>
                patient.Stage == PatientStage.WaitingLabAnalysis &&
                patient.RiskBasisPoints < 3_500));
        if (alreadyAtDecision)
        {
            simulation.AdvanceUntil(snapshot =>
                snapshot.ShiftComplete || snapshot.Phase == ShiftPhase.Closing, 2_000);
            return;
        }

        if (current.Phase == ShiftPhase.Closing)
        {
            simulation.AdvanceUntil(snapshot => snapshot.ShiftComplete, 2_000);
            return;
        }

        simulation.AdvanceUntil(snapshot =>
            snapshot.ShiftComplete ||
            snapshot.Phase == ShiftPhase.Closing ||
            snapshot.Lab.ResultBacklog >= 4 ||
            snapshot.Patients.Any(patient =>
                patient.Stage == PatientStage.WaitingLabAnalysis &&
                patient.RiskBasisPoints < 3_500), 2_000);
    }

    private static void Dispatch(HospitalSimulation simulation, ref long nextCommandId, LabDispatchPlan plan, TextWriter output)
    {
        Execute(simulation, SimulationCommand.DispatchLab(nextCommandId++, simulation.Tick, plan), output);
    }

    private static void Execute(HospitalSimulation simulation, SimulationCommand command, TextWriter output)
    {
        simulation.Submit(command);
        simulation.AdvanceTick();
        var result = simulation.GetCommandResult(command.CommandId);
        output.WriteLine($"{result.Status}：{result.Reason}");
    }

    private static void TogglePause(HospitalSimulation simulation, ref long nextCommandId)
    {
        var command = simulation.Paused
            ? SimulationCommand.Resume(nextCommandId++, simulation.Tick)
            : SimulationCommand.Pause(nextCommandId++, simulation.Tick);
        simulation.Submit(command);
        simulation.AdvanceTick();
    }
}
