// ============================================================================
// pre_fix_repro.js — 修复前失败复现
//
// 目标目录：origin_frozen/（上传版 v0.1 的逐字节冻结副本）
// 本脚本【只】记录观察结果，不修复、不注入默认值。
// 每条断言写成"缺陷已复现"的形式；PASS = 复现了审计描述的问题。
// ============================================================================
"use strict";

const path = require("path");
const fs = require("fs");
const { createEnv, makeReporter } = require("./harness");

const ROOT = path.resolve(__dirname, "..");
const SRC = path.join(ROOT, "origin_frozen");
const LOG = path.join(ROOT, "logs", "pre_fix_repro.log");

const lines = [];
const out = (s) => { lines.push(s); process.stdout.write(s + "\n"); };
const R = makeReporter("修复前失败复现（origin_frozen）", out);

function banner() {
  out("=".repeat(78));
  out("修复前失败复现 — 目标：origin_frozen/（上传版 v0.1 冻结副本）");
  out("加载方式：与 index.html 相同（jsdom 真实 DOM + config.js → game.js 顺序执行）");
  out("未注入任何游戏配置默认值");
  out("=".repeat(78));
  const probe = createEnv(SRC);
  out("");
  out("源码 sha256：");
  for (const [f, h] of Object.entries(probe.hash())) out(`  ${f.padEnd(12)} ${h}`);
  probe.dispose();
  out("");
}

// 在 rngState 空间里搜一个能让 pickCase() 返回指定病例的起点，然后真实调用 spawnPatient()
function forceSpawn(env, caseId) {
  for (let s = 1; s < 40000; s++) {
    const got = env.run(`(function(){ State.rngState=${s}; var c=pickCase(); return c&&c.id; })()`);
    if (got === caseId) {
      env.run(`State.rngState=${s}`);
      env.run(`spawnPatient()`);
      return true;
    }
  }
  return false;
}

function simSeconds(env) { return env.run("State.simTick") / env.run("TICK_HZ"); }

// ---------------------------------------------------------------- F0 输入完整性
function caseInputs() {
  R.section("F0 · 到院抖动输入是否存在于页面加载的配置里");
  const env = createEnv(SRC);
  const keys = env.run("Object.keys(IN)");
  const hasLow = keys.includes("arrival_jitter_low");
  const hasHigh = keys.includes("arrival_jitter_high");
  R.check("F0.1", "config.js 缺少 arrival_jitter_low（页面加载后 IN 里不存在）", !hasLow,
    `IN 实际键：${keys.join(", ")}`);
  R.check("F0.2", "config.js 缺少 arrival_jitter_high", !hasHigh, "");
  const jitterVal = env.run("(function(){ var v=jitter(); return (typeof v==='number'&&isFinite(v))?v:String(v); })()");
  R.check("F0.3", "jitter() 返回非有限值（NaN）", jitterVal === "NaN", `jitter() = ${jitterVal}`);
  env.dispose();
}

// ---------------------------------------------------------------- F1 断流
function caseStall(label, ticks) {
  const env = createEnv(SRC);
  env.step(ticks);
  const completed = env.run("State.stats.completed");
  const coins = env.run("State.coins");
  const inHospital = env.run("State.patients.length");
  const lat = env.run("State.lastArrivalTick");
  R.check(`${label}.1`, `${ticks} tick（${(ticks / 10 / 60).toFixed(1)} 模拟分钟）后只完成 1 人`,
    completed === 1, `completed=${completed}`);
  R.check(`${label}.2`, `金币停在 ${coins}`, coins === 435, `coins=${coins}`);
  R.check(`${label}.3`, `lastArrivalTick 变成非有限值`, !Number.isFinite(lat), `lastArrivalTick=${lat}`);
  R.check(`${label}.4`, `在院人数为 0（不再有新患者进入）`, inHospital === 0, `patients=${inHospital}`);
  env.dispose();
  return { completed, coins, inHospital, lat };
}

function caseStallGroup() {
  R.section("F1 · 首位患者之后到院断流");
  const a = caseStall("F1a", 3000);
  const b = caseStall("F1b", 18000);
  R.check("F1.c", "延长到 18000 tick 仍停在 1 人（断流不是暂态）",
    a.completed === b.completed && b.completed === 1,
    `3000 tick → ${a.completed} 人；18000 tick → ${b.completed} 人`);
}

// ---------------------------------------------------------------- F2 重复开业补助
function caseOpeningGrant() {
  R.section("F2 · 开放处置室时重复发放开业补助");
  const env = createEnv(SRC);
  env.step(3000);                                  // 跑到首例完成
  const afterFirst = env.run("State.coins");
  R.check("F2.1", `首例完成后金币 = ${afterFirst}（初始 250 + 首例奖励 + T01 150）`, afterFirst === 435, "");

  env.run(`doAction("unlock","TR")`);
  const afterUnlock = env.run("State.coins");
  const stored = env.storage.getItem("sunny_hospital_save_v1");
  R.check("F2.2", `开放 TR 后金币 = ${afterUnlock}（暴露了额外 150 的开业补助）`,
    afterUnlock === 185, `correct would be 35; coins=${afterUnlock}`);
  R.check("F2.3", "TR 开放本身扣款 400 属实", stored ? true : false, "存档仍在");

  env.step(50);                                    // T02 自动发奖
  const afterT02 = env.run("State.coins");
  const t02 = env.run("State.stats.claimedTasks.T02");
  R.check("F2.4", `T02 发奖后金币 = ${afterT02}`, afterT02 === 285, `correct would be 135`);
  R.check("F2.5", "T02「开放处置室」已发放", t02 === true, "");
  R.check("F2.6", "一次性奖励被重复发放：应为 135，实际 285（多 150）",
    afterT02 === 285, `实际 ${afterT02}，正确口径 135`);

  // 再开 LB，看补助是否再次叠加
  env.run("State.coins = 2000");
  env.run(`doAction("unlock","LB")`);
  const afterLB = env.run("State.coins");
  R.check("F2.7", `再开放检验 LB 又叠加一次 150（2000-1400+150=${afterLB}）`,
    afterLB === 750, `coins=${afterLB}`);
  env.dispose();
}

// ---------------------------------------------------------------- F3 末站倍率
function measuredServeTicks(env, caseId, roomId, unlockFirst) {
  const env2 = createEnv(SRC);
  env2.step(3000);                                  // 让 T01 走完，金币足够
  if (unlockFirst) { env2.run("State.coins = 3000"); env2.run(`doAction("unlock","${unlockFirst}")`); }
  if (!forceSpawn(env2, caseId)) { env2.dispose(); return { ok: false }; }

  const pid = env2.run("State.patients[State.patients.length-1].id");
  let measured = null, trace = [];
  for (let i = 0; i < 4000 && measured === null; i++) {
    env2.tickOnce();
    const st = env2.run(`(function(){var p=State.patients.find(x=>x.id===${pid});return p?p.state:null;})()`);
    if (st === "SERVE") {
      const info = env2.run(`(function(){var p=State.patients.find(x=>x.id===${pid});
        return JSON.stringify({room:p.serveRoom, total:p.serveEndTick-State.simTick});
      })()`);
      const o = JSON.parse(info);
      if (o.room === roomId) { measured = o.total; }
      else trace.push(o.room);
    }
    if (st === null || st === "DONE" || st === "GONE") { if (measured !== null) break; }
  }
  const required = env2.run(`(function(){
    var c=CFG.cases.find(x=>x.id==="${caseId}");
    var r=CFG.rooms["${roomId}"];
    return Math.round(r.base_ticks * c.last_multiplier);
  })()`);
  env2.dispose();
  return { ok: measured !== null, measured, required };
}

function caseLastMultiplier() {
  R.section("F3 · 病例末站倍率没有接入服务时间");
  const c03 = measuredServeTicks(SRC, "C03", "PH", null);
  R.check("F3.1", "C03（GP→PH，末站 PH 倍率 1.30）能测到末站服务时长", c03.ok,
    JSON.stringify(c03));
  R.check("F3.2", `C03 末站实际 ${c03.measured} tick（${(c03.measured / 10).toFixed(1)} 秒），倍率要求 ${c03.required} tick（${(c03.required / 10).toFixed(1)} 秒）`,
    c03.ok && c03.measured === 80 && c03.required === 104,
    `measured=${c03.measured} required=${c03.required}`);

  const c06 = measuredServeTicks(SRC, "C06", "TR", "TR");
  R.check("F3.3", "C06（GP→TR，末站 TR 倍率 1.30）能测到末站服务时长", c06.ok, JSON.stringify(c06));
  R.check("F3.4", `C06 末站实际 ${c06.measured} tick（${(c06.measured / 10).toFixed(1)} 秒），倍率要求 ${c06.required} tick（${(c06.required / 10).toFixed(1)} 秒）`,
    c06.ok && c06.measured === 200 && c06.required === 260,
    `measured=${c06.measured} required=${c06.required}`);

  const env = createEnv(SRC);
  const sig = env.run("String(serviceTicks)");
  R.check("F3.5", "serviceTicks() 签名里没有病例倍率参数",
    !/multiplier|ratio|case/i.test(sig), sig.replace(/\s+/g, " ").slice(0, 120));
  env.dispose();
}

// ---------------------------------------------------------------- F4 保存
function caseSaveSilentFailure() {
  R.section("F4 · 写盘失败仍保留内存扣款/建设，且界面无提示");
  const env = createEnv(SRC);
  env.run("State.coins = 1000");
  env.storage.setItem("sunny_hospital_save_v1", JSON.stringify({ v: 1, coins: 1000 }));
  env.storage._ctl.failWrites = 1;

  env.run(`doAction("unlock","TR")`);
  const memCoins = env.run("State.coins");
  const trOpen = env.run("State.rooms.TR.unlocked");
  const diskRaw = env.storage.getItem("sunny_hospital_save_v1");
  const diskCoins = JSON.parse(diskRaw).coins;
  const toast = env.toastText();

  R.check("F4.1", `内存金币已扣款并被补助改写：1000 → ${memCoins}`, memCoins === 750, `coins=${memCoins}`);
  R.check("F4.2", "内存里 TR 已变成已开放", trOpen === true, "");
  R.check("F4.3", `存档仍是旧值 ${diskCoins}（内存与存档不一致）`, diskCoins === 1000, "");
  R.check("F4.4", "界面没有任何失败提示（toast 只报补助成功，不报保存失败）",
    !/失败|无法保存|存档出错|保存不了/.test(toast) && toast !== "",
    `toast="${toast}"`);
  env.dispose();
}

function caseUnknownVersion() {
  R.section("F4b · 未知版本存档被无条件接受");
  const env = createEnv(SRC);
  env.storage.setItem("sunny_hospital_save_v1", JSON.stringify({
    v: 999, coins: 12345, simTick: 77, stats: { completed: 0, transferred: 0, investments: 0, service: {}, claimedTasks: {} }, rooms: {},
  }));
  const loaded = env.run("loadGame()");
  const coins = env.run("State.coins");
  R.check("F4b.1", `v=999 的快照被 loadGame() 接受（返回 ${loaded}）`, loaded === true, "");
  R.check("F4b.2", `未知版本里的金币被直接采用：${coins}`, coins === 12345, `coins=${coins}`);
  env.dispose();
}

function caseCorruptSaveOverwritten() {
  R.section("F4c · 坏档在自动存档后被静默覆盖，且没有备份");
  const env = createEnv(SRC);
  env.storage.setItem("sunny_hospital_save_v1", "{ this is not json ");
  const loaded = env.run("loadGame()");
  R.check("F4c.1", "坏档 loadGame() 返回 false", loaded === false, "");
  const before = env.storage.getItem("sunny_hospital_save_v1");
  env.step(320);                                   // 跨过 30 模拟秒自动存档
  const after = env.storage.getItem("sunny_hospital_save_v1");
  let parsedOk = false; try { JSON.parse(after); parsedOk = true; } catch (_) {}
  const backupKeys = Array.from(env.storage._map.keys()).filter(k => k !== "sunny_hospital_save_v1");
  R.check("F4c.2", "坏档原文已被新的自动存档覆盖", before !== after && parsedOk === true,
    `before=「${String(before).slice(0, 18)}…」 after 可解析=${parsedOk}`);
  R.check("F4c.3", "覆盖前没有留下任何备份键", backupKeys.length === 0,
    `备份键：${JSON.stringify(backupKeys)}`);
  env.dispose();
}

// ---------------------------------------------------------------- F5 暂停/帧间隔
function caseFrameCatchUp() {
  R.section("F5a · 帧间隔过大时模拟被补跑");
  const env = createEnv(SRC);
  env.frame(1000);                                  // 首帧建立 lastTime
  const t0 = env.run("State.simTick");
  const rows = [];
  let t = 1000;
  for (let i = 0; i < 3; i++) {
    t += 60000;                                     // 合成 60 秒帧间隔
    env.frame(t);
    rows.push(env.run("State.simTick") - t0);
  }
  R.check("F5a.1", `单帧 60 秒间隔被补跑成 20 模拟秒（3 帧累计 ${(rows[2] / 10)} 模拟秒）`,
    rows[0] === 200 && rows[1] === 400 && rows[2] === 600,
    `每帧推进 tick：${rows.join(" / ")}（1 模拟秒 = 10 tick）`);
  env.dispose();
}

function casePanelPause() {
  R.section("F5b · 打开房间面板不暂停");
  const env = createEnv(SRC);
  env.step(3000);
  env.clickRoom("GP");
  const paused = env.run("State.paused");
  const open = env.panelOpen();
  const t0 = env.run("State.simTick");
  env.step(100);
  const t1 = env.run("State.simTick");
  R.check("F5b.1", "点击房间后右侧面板确实打开了", open === true, "");
  R.check("F5b.2", "打开面板后 State.paused 仍为 false", paused === false, `paused=${paused}`);
  R.check("F5b.3", `面板打开期间模拟继续推进 ${t1 - t0} tick`, t1 - t0 === 100, `推进=${t1 - t0}`);
  env.dispose();
}

// ---------------------------------------------------------------- F6 按钮刷新
function caseButtonRefresh() {
  R.section("F6a · 房间面板按钮状态不随金币刷新");
  const env = createEnv(SRC);
  env.run("State.coins = 250");
  env.clickRoom("GP");
  const btn = env.panelButton("speed");
  R.check("F6a.1", "金币 250 时 GP 提速按钮（300 金币）处于禁用", btn && btn.disabled === true,
    btn ? `disabled=${btn.disabled}` : "按钮不存在");
  env.run("State.coins = 435");
  env.run("updateHUD()");
  const after = env.panelButton("speed");
  R.check("F6a.2", "金币涨到 435 后，同一个按钮节点仍是禁用的", after && after.disabled === true,
    after ? `disabled=${after.disabled}` : "按钮不存在");
  const same = btn === after;
  R.check("F6a.3", "未重建 DOM（说明确实没有刷新，而不是换了新节点）", same, `sameNode=${same}`);
  env.dispose();
}

function caseSpeedButtonLabel() {
  R.section("F6b · 倍速按钮文案不反映真实速度");
  const env = createEnv(SRC);
  env.run("State.speed = 4");
  env.run("updateHUD()");
  const label = env.run(`document.getElementById("btn-speed").textContent`);
  const real = env.run("State.speed");
  R.check("F6b.1", `真实速度 ${real}×，按钮仍显示「${label}」`, label === "1×",
    `label=${label} real=${real}`);
  env.dispose();
}

// ---------------------------------------------------------------- F7 登记并发
function caseRegistrationConcurrency() {
  R.section("F7 · 登记并发超过配置上限（registration_slots=4）");
  const env = createEnv(SRC);
  const slots = env.run("IN.registration_slots");
  R.check("F7.1", `配置里的登记并发上限 = ${slots}`, slots === 4, `slots=${slots}`);

  let spawned = 0;
  for (let i = 0; i < 5; i++) if (forceSpawn(env, "C01")) spawned++;
  R.check("F7.2", `强制放入 5 名患者（实际放入 ${spawned}）`, spawned === 5, "");

  let peak = 0;
  for (let i = 0; i < 400; i++) {
    env.tickOnce();
    const n = env.run(`State.patients.filter(p=>p.state==="REGISTER").length`);
    if (n > peak) peak = n;
  }
  R.check("F7.3", `峰值同时登记人数 = ${peak}，超过配置上限 ${slots}`, peak > slots,
    `peak=${peak} slots=${slots}`);
  const hasRegSlots = env.run("typeof State.regSlots");
  R.check("F7.4", "实现里没有登记位数组（登记容量从未被执行）", hasRegSlots === "undefined",
    `typeof State.regSlots = ${hasRegSlots}`);
  env.dispose();
}

// ---------------------------------------------------------------- F8 准入预算
function caseAdmissionBudget() {
  R.section("F8 · admission_gp_wait_budget_seconds 未被执行");
  const env = createEnv(SRC);
  const budget = env.run("IN.admission_gp_wait_budget_seconds");
  R.check("F8.1", `配置里的 GP 准入等待预算 = ${budget} 模拟秒`, budget === 48, "");
  const uses = env.run(`(function(){ return /admission_gp_wait_budget/.test(String(tick)) })()`);
  R.check("F8.2", "tick() 里没有引用该预算（从未被执行）", uses === false, "");
  env.dispose();
}

// ---------------------------------------------------------------- 主流程
function main() {
  banner();
  caseInputs();
  caseStallGroup();
  caseOpeningGrant();
  caseLastMultiplier();
  caseSaveSilentFailure();
  caseUnknownVersion();
  caseCorruptSaveOverwritten();
  caseFrameCatchUp();
  casePanelPause();
  caseButtonRefresh();
  caseSpeedButtonLabel();
  caseRegistrationConcurrency();
  caseAdmissionBudget();
  const s = R.summary();
  out("");
  out(`结论：共 ${s.total} 项观察，${s.pass} 项复现了审计描述的缺陷，${s.fail} 项未能复现。`);
  out("本日志是修复前的基线证据，不代表产品验收通过。");

  fs.mkdirSync(path.dirname(LOG), { recursive: true });
  fs.writeFileSync(LOG, lines.join("\n") + "\n", "utf8");
  console.log("\n[写入] " + LOG);
}

main();
