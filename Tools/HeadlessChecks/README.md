# 无端口逻辑校验

常规测试入口仍为 `dotnet test Tests/Game.Simulation.Tests/Game.Simulation.Tests.csproj`。

当沙盒不允许标准 testhost 绑定本机通信端口时，`sh Tools/check-simulation.sh` 在同一进程执行当前测试程序集的 Fact 和 InlineData Theory，使用原有 xUnit 断言。它不会开启测试宿主、监听端口或提升权限，也不安装依赖。测试项目的依赖需要已经还原。

支持当前套件使用的同步方法、Task/ValueTask 和 IDisposable；不提供完整 xUnit fixture、数据提供器、隔离或并行执行语义。跳过项和没有 InlineData 的 Theory 会明确失败，不静默计为通过。最终验证记录应区分此校验与标准 testhost 测试。

可追加类名筛选，例如 `sh Tools/check-simulation.sh ShiftReturnTests`。无匹配用例或任一断言失败会返回非零退出码。
