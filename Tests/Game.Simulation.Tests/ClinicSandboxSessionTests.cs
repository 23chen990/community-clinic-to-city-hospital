using Game.Simulation.Demo;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class ClinicSandboxSessionTests
{
    [Fact]
    public void ScriptedSessionShowsAProblemThenCompletesThePatientJourney()
    {
        using var input = new StringReader("n\nn\nr\nn\nd\nn\nn\nq\n");
        using var output = new StringWriter();

        ClinicSandboxSession.Run(input, output);

        var transcript = output.ToString();
        Assert.Contains("当前瓶颈：接待等待", transcript);
        Assert.Contains("患者001（等待接待 1 Tick）", transcript);
        Assert.Contains("接待开始", transcript);
        Assert.Contains("当前瓶颈：医生等待", transcript);
        Assert.Contains("接诊开始", transcript);
        Assert.Contains("诊疗完成", transcript);
        Assert.Contains("患者离开", transcript);
        Assert.Contains("沙盒结果：患者已完成一次诊疗并离开医院。", transcript);
    }

    [Fact]
    public void QueueSessionShowsTwoPatientsSharingReceptionAndDoctorCapacity()
    {
        using var input = new StringReader("n\nr\nn\nd\nr\nn\nd\nn\nn\n");
        using var output = new StringWriter();

        ClinicSandboxSession.Run(input, output, includeSecondPatient: true);

        var transcript = output.ToString();
        Assert.Contains("双患者排队压力", transcript);
        Assert.Contains("患者002", transcript);
        Assert.Contains("等待患者：2", transcript);
        Assert.Contains("接待状态：忙碌", transcript);
        Assert.Contains("已离开患者：2", transcript);
        Assert.Contains("沙盒结果：患者已完成一次诊疗并离开医院。", transcript);
    }

    [Fact]
    public void InvalidDoctorActionExplainsTheNextStepDuringRegistration()
    {
        using var input = new StringReader("n\nr\nd\nq\n");
        using var output = new StringWriter();

        ClinicSandboxSession.Run(input, output);

        Assert.Contains("患者正在登记，请先按 N 完成登记。", output.ToString());
    }
}
