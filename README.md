# 社区诊所到城市医院

当前阶段：M1 纯 C# 经营模拟验证。按产品负责人要求优先打通调度、完整白班和次日回流，暂不实现游戏 UI。尚未达到 M2 Unity 灰盒或正式可玩版本的验收标准。

产品规则以 [CURRENT](Docs/SSOT/CURRENT.md) 为准；视觉与交互状态见 [UX 索引](Docs/UX/INDEX.md)。`prototype/` 是另一玩法方向的旧原型，不代表当前正式规则。

## 逻辑沙盒

需要 .NET 10 SDK。在仓库根目录运行：

```sh
dotnet run --project src/Game.Simulation.Demo -- --play
```

这是文字逻辑沙盒，包含一个完整白班和下一营业日的新患者。`A` 推进模拟，`R` 风险优先，`S` 临时支援，`O` 外送未分析的普通样本，`P` 暂停/继续；结算后 `D` 安排下一日，`Q` 退出。调度结果由模拟判断，条件不满足时会给出原因。

次日回流具有概率，不保证某名患者回来。回来的患者保留已有检查结果与剩余诊疗工作；未完成离院不计为治愈出院。高风险患者仍走安全处置，当前没有正式晚班。

`--vertical` 为五患者基线验证；不带参数和 `--queue` 保留早期流程演示。

## 校验

```sh
dotnet test Tests/Game.Simulation.Tests/Game.Simulation.Tests.csproj
```

若沙盒不允许标准测试宿主建立本机通信，已还原依赖时可运行：

```sh
sh Tools/check-simulation.sh
```

该入口在同一进程执行当前 Fact/InlineData 和 xUnit 断言，不启动 testhost；支持范围与限制见 [校验说明](Tools/HeadlessChecks/README.md)。它不替代正式测试宿主、Unity 灰盒、设备验证或真人体验验收。
