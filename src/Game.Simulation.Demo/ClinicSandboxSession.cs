using Game.Simulation;

namespace Game.Simulation.Demo;

public static class ClinicSandboxSession
{
    public static void Run(TextReader input, TextWriter output)
    {
        Run(input, output, includeSecondPatient: false);
    }

    public static void Run(TextReader input, TextWriter output, bool includeSecondPatient)
    {
        var clinic = includeSecondPatient
            ? ClinicSandbox.CreateQueueScenario()
            : new ClinicSandbox();

        output.WriteLine(includeSecondPatient
            ? "M1-A 最小医院经营沙盒：双患者排队压力"
            : "M1-A 最小医院经营沙盒");
        output.WriteLine("目标：发现患者卡在哪里，并通过操作让患者完成诊疗后离开。\n");

        while (true)
        {
            output.WriteLine(ClinicDashboard.Render(clinic.Snapshot, clinic.Events));

            if (clinic.Snapshot.IsFinished)
            {
                output.WriteLine("沙盒结果：患者已完成一次诊疗并离开医院。");
                return;
            }

            output.WriteLine("可用操作：[N] 推进时间  [R] 开始接待  [D] 开始医生处理  [Q] 退出");
            output.Write("请输入操作：");

            var command = input.ReadLine()?.Trim().ToLowerInvariant();
            if (command is null or "q")
            {
                output.WriteLine("已退出沙盒。");
                return;
            }

            var actionSucceeded = command switch
            {
                "n" => AdvanceTime(clinic),
                "r" => clinic.StartReception(),
                "d" => clinic.StartDoctorVisit(),
                _ => false
            };

            if (!actionSucceeded)
            {
                output.WriteLine($"{RejectedActionMessage(command, clinic.Snapshot)}\n");
            }
        }
    }

    private static bool AdvanceTime(ClinicSandbox clinic)
    {
        clinic.AdvanceTime();
        return true;
    }

    private static string RejectedActionMessage(string? command, ClinicSnapshot snapshot)
    {
        if (command == "d" && snapshot.ReceptionBusy)
        {
            return "操作未执行：患者正在登记，请先按 N 完成登记。";
        }

        if (command == "d" && snapshot.WaitingPatients == 0)
        {
            return "操作未执行：目前没有等待医生的患者。";
        }

        if (command == "r" && snapshot.ReceptionBusy)
        {
            return "操作未执行：接待正在处理患者，请先按 N 推进时间。";
        }

        return "操作未执行：请观察布局、医院状态和当前瓶颈。";
    }
}
