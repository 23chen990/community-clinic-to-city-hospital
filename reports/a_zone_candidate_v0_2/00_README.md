# 《晴天小医院》A 区候选 v0.2.0 — 提交说明

日期：2026-10-01
候选版本：`0.2.0-a-zone`（`candidate = A-zone/v0.2.0`）
对应独立验收：《晴天小医院》prototype v0.1 独立验收（`Sunny_Hospital_Prototype_v0_1_Review.md`）

---

## 1. 唯一候选

**本次只有一个 A 区候选：`prototype/`。** 没有第二份平行分支、没有"新版本/旧版本"两份代码。
仓库里另外存在的那份源码是 `reports/a_zone_candidate_v0_2/origin_frozen/`，
它是**上传版 v0.1 的逐字节冻结副本，只用于失败复现对照，不是候选**，
不提供启动脚本，文件名与目录名都带 `origin_frozen` 前缀以免误用。
（`prototype/` 里原来的三张 v0.1 截图 `screenshot_02/03/04*.png` 也一并**移动**到了
`origin_frozen/`，没有删除——它们拍的是修复前的错误状态，留在候选目录会误导。
候选自己的截图在 `shots/`。）

| 文件 | sha256 |
|---|---|
| `index.html` | `5e00caff70c8ce9a4f2dbfb4df36086652919bdd8984389c76153aa20732be11` |
| `style.css` | `47f7c3ca61f6e045b060a44e89f2675641eac63b7b3cdde65ffd15e66f49f37b` |
| `config.js` | `5998f927539ea82c63b32013d0783d690f5b37b1eb484fb5fa41fa388ee43fc7` |
| `game.js` | `a00a392d75c821e9423b1ea0f1a821e422f2dc3f0d50afed849ba2c09becc14c` |
| `VERSION.txt` | `437b9074666ff9fe56d8e1e03f184bbf8550521b1dd4bcd85538560464337488` |

源码与配置同版本：`config.js` 的 `version`、`VERSION.txt`、`game.js` 运行时读取
（`CFG.version` / `CFG.candidate`）三者一致，回归项 `R0.2`/`R0.3`/`R0.6` 会守住这一点。

存档格式版本：`SAVE_VERSION = 2`。上传版 v1 存档会被拒绝并备份（见 FIXES.md P1-1）。

---

## 2. 怎么跑起来

**玩家视角**（Mac 双击）：

```
prototype/启动原型.command
```

**复现全部证据**：

```sh
sh reports/a_zone_candidate_v0_2/tests/run_all.sh
# 只做截图与量化、跳过录屏：
SKIP_RECORD=1 sh reports/a_zone_candidate_v0_2/tests/run_all.sh
```

依赖：Node（`/Users/kker/.workbuddy/binaries/node/versions/22.12.0/bin/node`）、
`jsdom` + `puppeteer` + `ffmpeg-static`（都在 `/Users/kker/.workbuddy/binaries/node/workspace/node_modules`）。
纯本地，不需要账号、不需要联网。

---

## 3. 提交物清单

| 类别 | 路径 | 说明 |
|---|---|---|
| 唯一 A 区候选 | `prototype/` | index.html / style.css / config.js / game.js / VERSION.txt / 启动原型.command |
| 逐问题修复记录 | `reports/a_zone_candidate_v0_2/FIXES.md` | 失败复现 → 根因 → 修正 → 回归 |
| 测试脚本 | `tests/harness.js` | 与页面同源的加载驱动（jsdom + VM，**不注入任何默认值**） |
| | `tests/pre_fix_repro.js` | 修复前失败复现（跑 origin_frozen） |
| | `tests/post_fix_regression.js` | 修复后回归（跑 prototype） |
| | `tests/browser_evidence.js` | 真实 Chromium：三视口截图 + 量化 + 录屏 |
| | `tests/run_all.sh` | 一键复现 |
| 原始日志 | `logs/pre_fix_repro.log` | 47 条观察，47 条复现缺陷 |
| | `logs/post_fix_regression.log` | 165 条断言，165 PASS（退出码 0） |
| | `logs/browser_evidence.log` | 三视口越界/字号/患者大小量化对比 |
| | `logs/browser_evidence.json` | 同上，机器可读 |
| 三横屏视口截图 | `shots/landscape_{1280x720,844x390,667x375}_after_{main,panel,locked,expanded}.png` | 候选 |
| | `shots/landscape_*_before_{main,panel,locked}.png` | 修复前对照 |
| 录屏 | `recording/opening_to_lab_844x390.mp4` | 844×390，连续开业 → 开放处置室 → 开放检验室 → 检验室实际接诊 |
| 冻结对照 | `origin_frozen/` | 上传版 v0.1 逐字节副本，**非候选** |

---

## 4. 测试口径（重要）

- 测试驱动加载的是 `prototype/index.html` 引用的**同一份** `config.js` 和 `game.js`，
  按页面相同的 `<script>` 顺序在同一个 JS 上下文里执行；
  只补 canvas 2D 桩、rAF、时钟、localStorage、逻辑视口这几项**浏览器表面**。
- **没有在任何测试脚本里补游戏默认值。** 缺字段就是缺字段。
- 反向证明：`R13` 会删掉 `config.js` 的到院抖动字段再加载一次，
  断言 `start()` 拒绝启动、报错指名缺哪两个字段、页面显示错误页。
  也就是说修复前那 47 条失败观察不是测试脚本造出来的。
- 测试脚本的退出码只代表脚本成败。`post_fix_regression.js` 有任何 FAIL 就以非 0 退出。
- 证据边界：真机、TapTap 宿主、Cocos、真实触屏手感、真实锁屏后台、真实磁盘配额 —— **全部未测**。
  后台行为是用 `visibilitychange` + 合成的 60 秒帧间隔验证**逻辑**，不冒充真实锁屏测试。

---

## 5. 未测项（如实标注）

1. 真机 iPhone / Android 横屏实测 —— **未测**
2. TapTap 普通小游戏宿主、打包、提审 —— **未测**
3. Cocos Creator + TypeScript 工程迁移 —— **未做**
4. 真实触屏手感、真实安全区（刘海/圆角/手势条）—— **未测**
5. 真实锁屏/切后台/系统回收后的恢复 —— **未测**
6. 浏览器 localStorage 真实配额耗尽、浏览器崩溃恢复 —— **未测**
7. 数值最优性（价格/时长/收益/投资间隔是否最优）—— **未证明**。本轮不改任何价格与时长，
   先恢复正确实现；真实投资间隔需要按 FIXES.md P1-2 修好后的版本重新观察再决定是否调数值。
8. 美术资源、音效、动效 —— 未做（按计划保持占位色块）。

---

## 6. 本轮边界声明

- 仍是 **四科室**（GP / PH / TR / LB）、**固定布局**、**投币建设**、**自动诊疗**。
- **没有**新增管理系统、后台报表页、施工/分期、材料、员工工资排班、库存、随机属性。
- **没有**改任何价格、服务时长、奖励金额来掩盖错误。
  `opening_grant` 的重复发放是**实现错误**，修的是实现，不是把奖励改小。
- B/C 区（IM / OB / RH）仍为 `locked` 占位，不可投币，命令层也会直接拒绝。
- 本轮之后**停止扩功能**。
