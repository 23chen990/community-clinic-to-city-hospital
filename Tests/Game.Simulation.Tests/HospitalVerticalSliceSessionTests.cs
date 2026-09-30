using Game.Simulation.Demo;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class HospitalVerticalSliceSessionTests
{
    [Fact]
    public void SessionShowsBottleneckInterventionAndShiftSettlement()
    {
        using var input = new StringReader("n\nn\nn\nn\nn\nn\ns\nn\nn\nn\nn\nq\n");
        using var output = new StringWriter();

        HospitalVerticalSliceSession.Run(input, output);

        var transcript = output.ToString();
        Assert.Contains("基础化验", transcript);
        Assert.Contains("当前瓶颈：结果发布", transcript);
        Assert.Contains("临时支援开始", transcript);
        Assert.Contains("结果返回门诊", transcript);
        Assert.Contains("治疗完成", transcript);
        Assert.Contains("药房完成", transcript);
        Assert.Contains("班次结算", transcript);
        Assert.Contains("已离开患者：3", transcript);
    }

    [Fact]
    public void SessionShowsOutsourcingAsAVisibleAlternative()
    {
        using var input = new StringReader("n\nn\nn\nn\nn\nn\n o\nn\nn\nn\nn\nn\n");
        using var output = new StringWriter();

        HospitalVerticalSliceSession.Run(input, output);

        var transcript = output.ToString();
        Assert.Contains("外送样本开始", transcript);
        Assert.Contains("外送处理中", transcript);
        Assert.Contains("外送结果返回", transcript);
        Assert.Contains("班次结算", transcript);
    }
}
