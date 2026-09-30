using Game.Simulation;

namespace Game.Simulation.Demo;

public static class HospitalVerticalSliceSession
{
    public static void Run(TextReader input, TextWriter output)
    {
        var slice = new HospitalVerticalSlice();

        output.WriteLine("M1-A 第一个完整经营纵切");
        output.WriteLine("目标：看懂患者流、发现化验瓶颈，并让本班患者完成诊疗。\n");

        while (true)
        {
            output.WriteLine(VerticalSliceDashboard.Render(slice.Snapshot, slice.Events));

            if (slice.Snapshot.ShiftComplete)
            {
                output.WriteLine("纵切结果：本班患者已完成诊疗并离开医院。");
                return;
            }

            output.WriteLine("可用操作：[N] 推进时间  [S] 临时支援  [O] 外送样本  [Q] 退出");
            output.Write("请输入操作：");
            var command = input.ReadLine()?.Trim().ToLowerInvariant();
            if (command is null or "q")
            {
                output.WriteLine("已退出纵切。");
                return;
            }

            var succeeded = command switch
            {
                "n" => AdvanceTime(slice),
                "s" => slice.StartTemporarySupport(),
                "o" => slice.StartOutsourcedSample(),
                _ => false
            };

            if (!succeeded)
            {
                output.WriteLine(RejectedActionMessage(command, slice.Snapshot));
            }
        }
    }

    private static bool AdvanceTime(HospitalVerticalSlice slice)
    {
        slice.AdvanceTime();
        return true;
    }

    private static string RejectedActionMessage(string? command, VerticalSliceSnapshot snapshot)
    {
        if (command == "s" && snapshot.ResultBacklog == 0)
        {
            return "操作未执行：当前没有结果发布积压，请先观察化验室状态。\n";
        }

        return "操作未执行：请观察地图、化验室状态和当前瓶颈。\n";
    }
}
