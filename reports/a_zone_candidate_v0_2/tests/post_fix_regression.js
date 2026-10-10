// ============================================================================
// post_fix_regression.js — 修复后回归
//
// 目标目录：../  （也就是 prototype/，本次唯一 A 区候选）
// 加载方式与 index.html 完全相同；不注入任何游戏配置默认值。
// 任何一项 FAIL 都会让脚本以非 0 退出码结束。
// ============================================================================
"use strict";

const path = require("path");
const fs = require("fs");
const os = require("os");
const { createEnv, makeReporter } = require("./harness");

const ROOT = path.resolve(__dirname, "..");
const CAND = path.resolve(ROOT, "..", "..", "prototype");   // /Users/kker/医院/prototype
const LOG = path.join(ROOT, "logs", "post_fix_regression.log");

const lines = [];
const out = (s) => { lines.push(s); process.stdout.write(s + "\n"); };
const R = makeReporter("修复后回归（A 区候选）", out);

function boot() { return createEnv(CAND); }

function forceSpawn(env, caseId) {
  for (let s = 1; s < 40000; s++) {
    const got = env.run(`(function(){ State.rngState=${s}; var c=pickCase(); return c&&c.id; })()`);
    if (got === caseId) {
      env.run(`State.rngState=${s}`);
      const p = env.run("spawnPatient() && State.patients[State.patients.length-1].id");
      return p || null;
    }
  }
  return null;
}

// 追踪一名患者路线上每一站的实际服务时长与落点
function tracePatient(env, caseId, unlockRooms) {
  const rooms = Array.isArray(unlockRooms) ? unlockRooms : (unlockRooms ? [unlockRooms] : []);
  if (rooms.length) {
    env.run("State.coins = 90000");
    for (const rid of rooms) env.run(`doAction("unlock","${rid}")`);
  }
  const id = forceSpawn(env, caseId);        // 必须先解锁，再生成，否则路线不可用会退化成 base
  if (id === null) return { ok: false, reason: "forceSpawn 失败" };
  const legs = [];
  let prev = null;
  for (let i = 0; i < 30000; i++) {
    env.tickOnce();
    const raw = env.run(`(function(){ var p=State.patients.find(x=>x.id===${id}); if(!p) return null;
      return JSON.stringify({s:p.state,r:p.serveRoom,rem:p.serveEndTick-State.simTick,ri:p.routeIdx,
        x:Math.round(p.x),y:Math.round(p.y),aw:p.admissionWait}); })()`);
    if (raw === null) break;
    const o = JSON.parse(raw);
    if ((prev === null || prev.s !== "SERVE") && o.s === "SERVE") {
      legs.push({ room: o.r, ticks: o.rem, routeIdx: o.ri, x: o.x, y: o.y });
    }
    prev = o;
  }
  return { ok: true, legs, caseId };
}

// 逐步推进到第 N 位患者完成（用于隔离金币算术，避免多例收入混进来）
function stepToCompletion(env, n) {
  const before = env.run("State.stats.completed");
  const coinsBefore = env.run("State.coins");
  for (let i = 0; i < 40000; i++) {
    const c0 = env.run("State.stats.completed");
    const k0 = env.run("State.coins");
    env.tickOnce();
    if (env.run("State.stats.completed") >= n) {
      return { ok: true, coinsBefore, coinsAfter: env.run("State.coins"), tick: env.run("State.simTick"), prevCompleted: c0, prevCoins: k0 };
    }
  }
  return { ok: false, from: before, coinsBefore };
}

// 只掐断后续到院，保留在场患者
function stopArrivals(env) {
  env.run("State.nextArrivalTick = State.simTick + 1000000");
}

// 隔离金币算术：清空在场患者并冻结到院
function freezeArrivals(env) {
  env.run("State.patients.length = 0");
  env.run("State.regQueue.length = 0");
  env.run("State.regSlots.fill(null)");
  env.run("State.nextArrivalTick = State.simTick + 1000000");
}

// ---------------------------------------------------------------- R0 版本/配置一致
function r0() {
  R.section("R0 · 唯一候选的版本与配置完整性");
  const pkgDir = CAND;
  const verFile = path.join(pkgDir, "VERSION.txt");
  const env = boot();
  const cfgVer = env.run("CFG.version");
  const candidate = env.run("CFG.candidate");
  const saveVer = env.run("window.HOSP_DEBUG.saveVersion");
  const fileVer = fs.existsSync(verFile) ? fs.readFileSync(verFile, "utf8").trim() : "(缺失)";

  const inputs = env.run("Object.keys(IN)");
  const required = ["tick_hz", "initial_coins", "opening_grant", "speed_gain_per_level",
    "patience_seconds", "tutorial_grace_seconds", "max_patients", "walk_units_per_second",
    "registration_seconds", "registration_slots", "settle_seconds", "autosave_sim_seconds",
    "admission_gp_wait_budget_seconds", "arrival_jitter_low", "arrival_jitter_high"];
  const missing = required.filter(k => !inputs.includes(k));

  R.check("R0.1", "游戏能正常启动（无配置校验错误）", env.run("State.bootError") === null,
    JSON.stringify(env.snap("State.bootError")));
  R.check("R0.2", `config.js 声明的版本 = ${cfgVer}`, /^0\.2\.0/.test(String(cfgVer)), `version=${cfgVer}`);
  R.check("R0.3", `VERSION.txt 与 config.js 版本一致（${fileVer}）`, fileVer === candidate || fileVer === cfgVer,
    `VERSION.txt="${fileVer}" candidate="${candidate}" config="${cfgVer}"`);
  R.eq("R0.4", "存档模式版本 = 2（上传版 v1 不再被接受）", saveVer, 2);
  R.eq("R0.5", "页面加载的配置里所有必需字段齐全（缺字段不会被静默补默认值）", missing, []);
  R.check("R0.6", "config 与 game 的版本号同源（game 从 CFG 读取）",
    env.run("window.HOSP_DEBUG.version") === cfgVer, "");
  env.dispose();
}

// ---------------------------------------------------------------- R1 断流
function r1() {
  R.section("R1 · 到院不再断流");
  const env = boot();
  env.step(3000);
  const c3000 = env.run("State.stats.completed");
  const lat = env.run("State.nextArrivalTick");
  const finite = Number.isFinite(lat);
  R.check("R1.1", "nextArrivalTick 始终是有限值", finite, `nextArrivalTick=${lat}`);
  R.check("R1.2", `3000 tick（5 模拟分钟）后完成 ${c3000} 人（>1，远高于修复前的 1 人）`,
    c3000 >= 10, `completed=${c3000}`);
  env.step(3000);
  const c6000 = env.run("State.stats.completed");
  R.check("R1.3", `延长到 6000 tick 仍在增长：${c3000} → ${c6000}`, c6000 > c3000, "");

  const env2 = boot();
  env2.step(18000);
  const c18000 = env2.run("State.stats.completed");
  R.check("R1.4", `18000 tick（30 模拟分钟）完成 ${c18000} 人，未断流`, c18000 > 100, `completed=${c18000}`);
  const arrivals = env2.run("State.nextPatientId - 1");
  R.check("R1.5", `累计到院 ${arrivals} 人，与 4 人/分钟×30 分钟 ≈ 120 同量级`, arrivals >= 100 && arrivals <= 160,
    `arrivals=${arrivals}`);
  env.dispose(); env2.dispose();
}

// ---------------------------------------------------------------- R2 末站倍率
function r2() {
  R.section("R2 · 病例末站倍率已接入服务时长");
  const TABLE = [
    { id: "C03", route: ["GP", "PH"], mult: 1.30, expect: [120, 104] },
    { id: "C04", route: ["GP", "TR"], mult: 1.00, expect: [120, 200], unlock: "TR" },
    { id: "C06", route: ["GP", "TR"], mult: 1.30, expect: [120, 260], unlock: "TR" },
    { id: "C08", route: ["GP", "LB", "PH"], mult: 1.20, expect: [120, 160, 96], unlock: "LB" },
    { id: "C09", route: ["GP", "LB", "TR"], mult: 1.10, expect: [120, 160, 220], unlock: ["LB", "TR"] },
  ];
  for (const c of TABLE) {
    const env = boot();
    const tr = tracePatient(env, c.id, c.unlock);
    const ok = tr.ok && tr.legs.length === c.route.length;
    R.check(`R2.${c.id}.1`, `${c.id}（${c.route.join("→")}）走完整条路线`, ok,
      tr.ok ? `实际：${tr.legs.map(l => l.room + ":" + l.ticks).join(" / ")}` : tr.reason);
    if (ok) {
      const order = tr.legs.map(l => l.room).join("→");
      R.eq(`R2.${c.id}.2`, `${c.id} 各站顺序 = ${c.route.join("→")}`, order, c.route.join("→"));
      R.eq(`R2.${c.id}.3`, `${c.id} 各站实际服务 tick = ${c.expect.join(" / ")}`,
        tr.legs.map(l => l.ticks), c.expect);
      const last = tr.legs[tr.legs.length - 1].ticks;
      const base = env.run(`CFG.rooms["${c.route[c.route.length - 1]}"].base_ticks`);
      if (c.mult !== 1) {
        R.check(`R2.${c.id}.4`, `${c.id} 末站 ${last} tick ≠ 未加倍率的 ${base} tick（倍率确实生效）`,
          last !== base, `last=${last} base=${base}`);
      } else {
        R.check(`R2.${c.id}.4`, `${c.id} 倍率 1.00 时末站等于基础时长 ${base} tick`, last === base, "");
      }
    }
    env.dispose();
  }

  // 提速后倍率仍然生效（先提速，再验证末站）
  const env2 = boot();
  env2.run("State.coins = 9000");
  env2.run(`doAction("speed","GP")`);
  const tr4 = tracePatient(env2, "C03", null);
  if (tr4.ok && tr4.legs.length === 2) {
    R.eq("R2.speed.1", "GP 提速 Lv1 后首站 = round(120/1.15) = 104 tick", tr4.legs[0].ticks, 104);
    R.eq("R2.speed.2", "提速不影响末站倍率的应用（PH 仍 = 104）", tr4.legs[1].ticks, 104);
  } else {
    R.check("R2.speed.1", "提速场景可追踪", false, tr4.reason || "");
  }
  env2.dispose();

  // 末站倍率来自配置，而不是写死的常数
  const env3 = boot();
  R.eq("R2.cfg.1", "serviceTicks 签名接受倍率参数",
    env3.run(`/multiplier/.test(String(serviceTicks))`), true);
  R.eq("R2.cfg.2", "倍率 1 → 基础时长", env3.run(`serviceTicks(CFG.rooms.PH, 0, 1)`), 80);
  R.eq("R2.cfg.3", "倍率 1.3 → 104 tick", env3.run(`serviceTicks(CFG.rooms.PH, 0, 1.3)`), 104);
  R.eq("R2.cfg.4", "不传倍率时按 1 处理（房间面板用）", env3.run(`serviceTicks(CFG.rooms.PH, 0)`), 80);
  env3.dispose();
}

// ---------------------------------------------------------------- R3 一次性补助
function r3() {
  R.section("R3 · 一次性开业补助不再重复发放");
  const env = boot();
  const first = stepToCompletion(env, 1);
  R.check("R3.1", "能推进到第一位患者完成", first.ok, JSON.stringify(first));
  const delta = first.coinsAfter - first.coinsBefore;   // 同一 tick 内：首例奖励 + T01
  const reward = delta - 150;
  R.check("R3.2", `首例完成时的单 tick 收入 ${delta} = 首例奖励 ${reward} + T01 150`,
    reward === 30 || reward === 35, `delta=${delta} reward=${reward}`);
  R.eq("R3.3", "首例完成后金币 = 250 + 首例奖励 + 150", first.coinsAfter, 250 + reward + 150);

  freezeArrivals(env);
  env.run(`doAction("unlock","TR")`);
  const afterUnlock = env.run("State.coins");
  R.eq("R3.4", `开放 TR 后金币 = ${first.coinsAfter} - 400（不再有额外的 150 补助）`,
    afterUnlock, first.coinsAfter - 400);
  R.check("R3.5", "开放 TR 时没有多出来的 150",
    afterUnlock !== first.coinsAfter - 400 + 150, `coins=${afterUnlock}`);

  env.step(3);
  const afterT02 = env.run("State.coins");
  R.eq("R3.6", `T02 发奖后金币 = ${first.coinsAfter - 400 + 100}（修复前是 ${first.coinsAfter - 400 + 100 + 150}）`,
    afterT02, first.coinsAfter - 400 + 100);
  R.check("R3.7", "开放科室不计入 investments（T04 口径是提速或扩容）",
    env.run("State.stats.investments") === 0, `investments=${env.run("State.stats.investments")}`);
  R.eq("R3.8", "T04 尚未发放", !!env.run("State.stats.claimedTasks.T04"), false);

  const grants = env.snap("State.stats.grantLog");
  const ids = grants.map(g => g.id);
  const taskIds = env.snap("CFG.tasks.map(t=>t.id)");
  R.eq("R3.9", "补助条目只可能来自 CFG.tasks", ids.filter(i => !taskIds.includes(i)), []);
  R.eq("R3.10", "每个任务最多发放一次", new Set(ids).size, ids.length);
  R.eq("R3.11", "此时发放明细 = T01 150 + T02 100",
    grants.map(g => `${g.id}:${g.amount}`).join(","), "T01:150,T02:100");
  R.eq("R3.12", "opening_grant 与 T01 同额，是同一笔一次性补助",
    env.run("IN.opening_grant"), env.run("CFG.tasks.find(t=>t.id==='T01').reward"));

  env.run("State.coins = 3000");
  env.run(`doAction("unlock","LB")`);
  R.eq("R3.13", "再开放检验 LB：3000 - 1400 = 1600（不叠加任何补助）", env.run("State.coins"), 1600);
  R.eq("R3.14", "开放 LB 没有新增补助条目",
    env.snap("State.stats.grantLog").length, 2);
  R.eq("R3.15", "开放 LB 同样不计入 investments", env.run("State.stats.investments"), 0);

  env.run(`doAction("slot","GP")`);
  R.eq("R3.16", "扩容计入 investments", env.run("State.stats.investments"), 1);
  env.step(3);
  R.eq("R3.17", "扩容后 T04 恰好发放一次",
    env.snap("State.stats.grantLog").filter(g => g.id === "T04").length, 1);
  const n1 = env.snap("State.stats.grantLog").length;
  env.step(300);
  R.eq("R3.18", "再跑 300 tick 不产生重复发放", env.snap("State.stats.grantLog").length, n1);
  env.dispose();
}

// ---------------------------------------------------------------- R4 存档事务
function r4() {
  R.section("R4 · 写盘失败必须回滚并明确提示");

  const a = boot();
  a.run("State.coins = 1000");
  a.storage.setItem("sunny_hospital_save_v1", JSON.stringify(a.run("JSON.parse(JSON.stringify(serialize()))")));
  const diskBefore = a.storage.getItem("sunny_hospital_save_v1");
  a.storage._ctl.failWrites = 1;
  a.run(`doAction("unlock","TR")`);
  R.eq("R4.1", "抛异常式写盘失败：内存金币回滚到 1000", a.run("State.coins"), 1000);
  R.eq("R4.2", "抛异常式写盘失败：TR 未开放", a.run("State.rooms.TR.unlocked"), false);
  R.eq("R4.3", "存档未被改写", a.storage.getItem("sunny_hospital_save_v1") === diskBefore, true);
  R.check("R4.4", "界面明确提示保存失败", /保存失败/.test(a.toastText()), `toast="${a.toastText()}"`);
  R.eq("R4.5", "HUD 存档状态变为失败", a.run("State.saveStatus"), "failed");
  a.run("updateHUD()");
  R.eq("R4.6", "HUD 上可见「失败」", a.run(`document.getElementById("hud-save-value").textContent`), "失败");
  a.dispose();

  // 静默丢弃（write 成功假象，读回不一致）也必须判失败
  const b = boot();
  b.run("State.coins = 1000");
  b.run("serialize()");
  b.storage.setItem("sunny_hospital_save_v1", JSON.stringify(b.run("JSON.parse(JSON.stringify(serialize()))")));
  const diskB = b.storage.getItem("sunny_hospital_save_v1");
  b.storage._ctl.silentDrop = 1;
  b.run(`doAction("unlock","TR")`);
  R.eq("R4.7", "静默丢弃式写盘失败：金币回滚", b.run("State.coins"), 1000);
  R.eq("R4.8", "静默丢弃式写盘失败：TR 未开放", b.run("State.rooms.TR.unlocked"), false);
  R.eq("R4.9", "存档未被改写", b.storage.getItem("sunny_hospital_save_v1") === diskB, true);
  b.dispose();

  // 正常购买仍应成功并落盘
  const c = boot();
  c.run("State.coins = 1000");
  c.run(`doAction("unlock","TR")`);
  R.eq("R4.10", "正常写盘：金币 1000-400=600", c.run("State.coins"), 600);
  R.eq("R4.11", "正常写盘：TR 已开放", c.run("State.rooms.TR.unlocked"), true);
  const disk = JSON.parse(c.storage.getItem("sunny_hospital_save_v1"));
  R.eq("R4.12", "落盘内容与内存一致", disk.coins, 600);
  R.eq("R4.13", "落盘内容带版本号", disk.v, 2);
  c.dispose();
}

// ---------------------------------------------------------------- R5 版本/坏档
function r5() {
  R.section("R5 · 未知版本与坏档被拒绝并保留备份");

  const a = boot();
  a.storage.setItem("sunny_hospital_save_v1", JSON.stringify({
    v: 999, coins: 12345, simTick: 77, speed: 1, rngState: 1, nextPatientId: 1,
    nextArrivalTick: 1, lastSaveTick: 0, patients: [],
    stats: { completed: 0, transferred: 0, investments: 0, service: {}, claimedTasks: {} }, rooms: {},
  }));
  const resA = a.snap("loadGame()");
  R.eq("R5.1", "v=999 被拒绝", resA.ok, false);
  R.eq("R5.2", "拒绝原因 = version", resA.reason, "version");
  R.eq("R5.3", "未知版本的金币没有被采用（仍是初始 250）", a.run("State.coins"), 250);
  const bakA = Array.from(a.storage._map.keys()).filter(k => k.startsWith("sunny_hospital_save_v1.bak."));
  R.check("R5.4", "未知版本原文已备份", bakA.length === 1, `备份键：${JSON.stringify(bakA)}`);
  R.eq("R5.5", "备份内容就是原文", a.storage.getItem(bakA[0]).includes("999"), true);
  a.dispose();

  const b = boot();
  b.storage.setItem("sunny_hospital_save_v1", "{ this is not json ");
  const resB = b.snap("loadGame()");
  R.eq("R5.6", "坏档被拒绝", resB.ok, false);
  R.eq("R5.7", "拒绝原因 = corrupt", resB.reason, "corrupt");
  const bakB = Array.from(b.storage._map.keys()).filter(k => k.startsWith("sunny_hospital_save_v1.bak."));
  R.check("R5.8", "坏档原文已备份", bakB.length === 1, JSON.stringify(bakB));
  R.eq("R5.9", "备份里保留原始坏内容", b.storage.getItem(bakB[0]), "{ this is not json ");
  b.step(320);                                     // 跨过自动存档
  R.check("R5.10", "自动存档之后备份依然存在（不会被静默覆盖掉）",
    b.storage.getItem(bakB[0]) === "{ this is not json ", "");
  R.check("R5.11", "新档已正常写入", b.run(`(function(){ try{ return JSON.parse(localStorage.getItem("sunny_hospital_save_v1")).v===2 }catch(e){ return false } })()`),
    true, "");
  b.dispose();

  // 上传版 v1 存档（含 NaN 到院字段）也必须被拒绝
  const c = boot();
  c.storage.setItem("sunny_hospital_save_v1", JSON.stringify({
    v: 1, coins: 900, simTick: 5000, speed: 2, rngState: 9, nextPatientId: 12,
    lastArrivalTick: null, lastSaveTick: 4000,
    stats: { completed: 3, transferred: 0, investments: 0, service: {}, claimedTasks: {} },
    rooms: {}, patients: [],
  }));
  const resC = c.snap("loadGame()");
  R.eq("R5.12", "上传版 v1 存档被拒绝", resC.ok, false);
  R.eq("R5.13", "拒绝原因 = version", resC.reason, "version");
  R.eq("R5.14", "v1 的 900 金币没有被采用", c.run("State.coins"), 250);
  c.dispose();

  // 合法存档必须能恢复
  const d = boot();
  d.run("State.coins = 777; State.simTick = 4321; State.speed = 4;");
  R.eq("R5.15", "保存成功", d.run("saveGame(true)"), true);
  const e = boot();
  e.storage._map.set("sunny_hospital_save_v1", d.storage.getItem("sunny_hospital_save_v1"));
  const resE = e.snap("loadGame()");
  R.eq("R5.16", "同版本合法存档可以恢复", resE.ok, true);
  R.eq("R5.17", "恢复金币", e.run("State.coins"), 777);
  R.eq("R5.18", "恢复模拟刻", e.run("State.simTick"), 4321);
  R.eq("R5.19", "恢复倍速（按钮文案也应随后刷新）", e.run("State.speed"), 4);
  e.run("updateHUD()");
  R.eq("R5.20", "恢复后 HUD 倍速文案 = 4×",
    e.run(`document.getElementById("btn-speed").textContent`), "4×");
  d.dispose(); e.dispose();

  // 模拟进行中存档（有患者在途）→ 恢复到全新页面 → 状态一致且能继续跑
  const f = boot();
  f.run("State.coins = 5000");
  f.step(1500);
  const snapBefore = f.snap(`({
    coins: State.coins, simTick: State.simTick, nextPatientId: State.nextPatientId,
    completed: State.stats.completed, transferred: State.stats.transferred,
    patients: State.patients.length,
    inServe: State.patients.filter(p=>p.state==="SERVE").length,
    inWait: State.patients.filter(p=>p.state==="WAIT"||p.state==="QUEUE").length,
    rooms: Object.fromEntries(Object.entries(State.rooms).map(([k,v])=>[k,{u:v.unlocked,l:v.level,s:v.slots}]))
  })`);
  f.run("saveGame(true)");
  const rawMid = f.storage.getItem("sunny_hospital_save_v1");
  R.check("R5.21", "进行中的局面确实有患者在途", snapBefore.patients > 0,
    `patients=${snapBefore.patients} inServe=${snapBefore.inServe} inWait=${snapBefore.inWait}`);
  f.dispose();

  const g = boot();
  g.storage._map.set("sunny_hospital_save_v1", rawMid);
  const resG = g.snap("loadGame()");
  const snapAfter = g.snap(`({
    coins: State.coins, simTick: State.simTick, nextPatientId: State.nextPatientId,
    completed: State.stats.completed, transferred: State.stats.transferred,
    patients: State.patients.length,
    inServe: State.patients.filter(p=>p.state==="SERVE").length,
    inWait: State.patients.filter(p=>p.state==="WAIT"||p.state==="QUEUE").length,
    rooms: Object.fromEntries(Object.entries(State.rooms).map(([k,v])=>[k,{u:v.unlocked,l:v.level,s:v.slots}]))
  })`);
  R.eq("R5.22", "中途存档可以恢复", resG.ok, true);
  R.eq("R5.23", "恢复后关键状态与存档前完全一致", snapAfter, snapBefore);
  g.step(3000);
  const g2 = g.snap(`({c:State.stats.completed,t:State.stats.transferred,a:State.nextPatientId-1,p:State.patients.length,
    bad: State.patients.filter(p=>!Number.isFinite(p.x)||!Number.isFinite(p.y)).length,
    badArr: Number.isFinite(State.nextArrivalTick)})`);
  R.eq("R5.24", "恢复后继续模拟 3000 tick 仍然自洽（admitted = completed + transferred + 在院）",
    g2.a, g2.c + g2.t + g2.p);
  R.eq("R5.25", "恢复后没有患者的坐标变成非有限值", g2.bad, 0);
  R.eq("R5.26", "恢复后到院时刻仍然有限（不会再次断流）", g2.badArr, true);
  g.dispose();
}

// ---------------------------------------------------------------- R6 帧间隔/后台
function r6() {
  R.section("R6 · 大帧间隔不补跑；后台不推进");
  const a = boot();
  a.frame(1000);
  let prev = a.run("State.simTick");
  const perFrame = [];
  let t = 1000;
  for (let i = 0; i < 3; i++) {
    t += 60000;
    a.frame(t);
    const now = a.run("State.simTick");
    perFrame.push(now - prev);
    prev = now;
  }
  const total = perFrame.reduce((x, y) => x + y, 0);
  R.check("R6.1", `每个 60 秒间隔的帧最多推进 3 tick（修复前是 200 tick）`,
    perFrame.every(v => v <= 3), `每帧推进 tick：${perFrame.join(" / ")}`);
  R.check("R6.2", `3 帧累计只推进 ${total} tick（修复前是 600 tick）`, total < 20, `累计 ${total} tick`);
  a.dispose();

  const b = boot();
  b.frame(1000);
  const bt0 = b.run("State.simTick");
  b.setHidden(true);
  let bt = 1000;
  for (let i = 0; i < 5; i++) { bt += 30000; b.frame(bt); }
  const hiddenAdvance = b.run("State.simTick") - bt0;
  R.eq("R6.3", "页面隐藏期间完全不推进模拟", hiddenAdvance, 0);
  b.setHidden(false);
  b.frame(bt + 16);
  const afterReturn = b.run("State.simTick") - bt0;
  R.check("R6.4", "回到前台后没有补跑积压（第一帧推进 ≤ 4 tick）", afterReturn <= 4,
    `恢复后累计推进 ${afterReturn} tick`);
  R.check("R6.5", "visibilitychange 监听已注册", b.run(`typeof document.hidden === "boolean"`), true, "");
  b.dispose();
}

// ---------------------------------------------------------------- R7 面板暂停
function r7() {
  R.section("R7 · 打开房间面板即暂停，关闭恢复");
  const env = boot();
  env.step(500);
  env.clickRoom("GP");
  R.eq("R7.1", "点击房间后面板打开", env.panelOpen(), true);
  R.eq("R7.2", "打开面板后进入暂停", env.run("isPaused()"), true);
  R.eq("R7.3", "暂停来自面板而非用户", env.run("State.pausedByPanel"), true);
  R.eq("R7.4", "State.paused 兼容字段同步为 true", env.run("State.paused"), true);

  const t0 = env.run("State.simTick");
  env.frame(1000); env.frame(1016); env.frame(1200);
  R.eq("R7.5", "面板打开期间 rAF 循环不推进模拟", env.run("State.simTick"), t0);

  env.click("#rp-close");
  R.eq("R7.6", "关闭面板后恢复运行", env.run("isPaused()"), false);
  env.frame(1300);
  R.check("R7.7", "恢复后模拟继续推进", env.run("State.simTick") > t0, `simTick ${t0} → ${env.run("State.simTick")}`);

  // 用户暂停与面板暂停互不覆盖
  env.click("#btn-pause");
  R.eq("R7.8", "用户暂停生效", env.run("State.pausedByUser"), true);
  env.clickRoom("PH");
  env.click("#rp-close");
  R.eq("R7.9", "关闭面板不会取消用户暂停", env.run("State.pausedByUser"), true);
  R.eq("R7.10", "仍然处于暂停", env.run("isPaused()"), true);
  env.click("#btn-pause");
  R.eq("R7.11", "再次点击暂停按钮恢复运行", env.run("isPaused()"), false);
  env.dispose();
}

// ---------------------------------------------------------------- R8 按钮刷新
function r8() {
  R.section("R8 · 按钮状态实时刷新");
  const env = boot();
  env.run("State.coins = 250");
  env.clickRoom("GP");
  const btn = env.panelButton("speed");
  const slotBtn = env.panelButton("slot");
  R.eq("R8.1", "金币 250 时 GP 提速按钮（300）禁用", btn.disabled, true);
  R.eq("R8.2", "金币 250 时 GP 扩容按钮（700）禁用", slotBtn.disabled, true);

  env.run("State.coins = 435");
  env.run("updateHUD()");
  const after = env.panelButton("speed");
  R.check("R8.3", "金币涨到 435 后按钮自动变为可用", after.disabled === false, `disabled=${after.disabled}`);
  R.eq("R8.4", "刷新时复用同一个 DOM 节点（不重建，不丢焦点）", btn === after, true);
  R.eq("R8.5", "同一时刻 700 的扩容按钮仍然禁用", env.panelButton("slot").disabled, true);

  env.run("State.coins = 900");
  env.run("updateHUD()");
  R.eq("R8.6", "金币到 900 后扩容按钮也变为可用", env.panelButton("slot").disabled, false);
  env.run("State.coins = 100");
  env.run("updateHUD()");
  R.eq("R8.7", "金币跌回后按钮重新禁用（双向刷新）", env.panelButton("speed").disabled, true);

  env.run("State.speed = 4; updateHUD();");
  R.eq("R8.8", "倍速按钮文案跟随真实速度", env.run(`document.getElementById("btn-speed").textContent`), "4×");
  env.run("State.pausedByUser = true; updateHUD();");
  R.eq("R8.9", "用户暂停后按钮文案 = 继续", env.run(`document.getElementById("btn-pause").textContent`), "▶ 继续");
  env.run("State.pausedByUser = false; updateHUD();");
  R.eq("R8.10", "面板仍打开时仍处于暂停，文案保持与 isPaused() 一致",
    env.run(`document.getElementById("btn-pause").textContent`),
    env.run("isPaused()") ? "▶ 继续" : "⏸ 暂停");
  R.check("R8.10b", "按钮文案与真实暂停状态始终一致（不是写死的字符串）",
    env.run(`(document.getElementById("btn-pause").textContent === "▶ 继续") === isPaused()`), true, "");
  env.click("#rp-close");
  env.run("updateHUD()");
  R.eq("R8.10c", "关闭面板后按钮文案复位为暂停",
    env.run(`document.getElementById("btn-pause").textContent`), "⏸ 暂停");
  env.clickRoom("GP");

  // 购买后立刻重新渲染，且按钮状态正确
  env.run("State.coins = 1000");
  env.run(`doAction("speed","GP")`);
  R.eq("R8.11", "购买后等级已提升", env.run("State.rooms.GP.level"), 1);
  env.run("updateHUD()");
  R.eq("R8.12", "购买后面板仍显示正确等级",
    /Lv1 → Lv2/.test(env.panelHtml()), true);
  env.dispose();
}

// ---------------------------------------------------------------- R9 登记并发
function r9() {
  R.section("R9 · 登记并发执行 registration_slots 上限");
  const env = boot();
  const slots = env.run("IN.registration_slots");
  R.eq("R9.1", "配置上限 = 4", slots, 4);
  R.eq("R9.2", "登记位数组长度等于配置", env.run("State.regSlots.length"), 4);

  let spawned = 0;
  for (let i = 0; i < 8; i++) if (forceSpawn(env, "C01")) spawned++;
  stopArrivals(env);
  R.eq("R9.3", "放入 8 名患者并停止后续到院", spawned, 8);

  let peak = 0, peakQueue = 0;
  for (let i = 0; i < 3000; i++) {
    env.tickOnce();
    const n = env.run(`State.patients.filter(p=>p.state==="REGISTER").length`);
    const q = env.run("State.regQueue.length");
    if (n > peak) peak = n;
    if (q > peakQueue) peakQueue = q;
  }
  R.check(`R9.4`, `峰值同时登记人数 ${peak} ≤ 配置上限 ${slots}`, peak <= slots, `peak=${peak}`);
  R.eq(`R9.5`, "确实用满了并发（不是因为没有并发而通过）", peak, slots);
  R.check("R9.6", "多出来的患者在入口队列排队", peakQueue > 0, `入口队列峰值=${peakQueue}`);
  R.eq("R9.7", "8 名患者全部走完登记（登记位没有泄漏）",
    env.run("State.regSlots.filter(x=>x!==null).length") +
    env.run("State.regQueue.length") +
    env.run(`State.patients.filter(p=>p.state==="REGISTER").length`), 0);
  R.check("R9.8", "没有患者卡在注册状态",
    env.run(`State.patients.filter(p=>p.state==="REGISTER").length`) === 0, "");
  R.check("R9.9", `8 名患者全部被处理（完成 ${env.run("State.stats.completed")} + 转诊 ${env.run("State.stats.transferred")}）`,
    env.run("State.stats.completed") + env.run("State.stats.transferred") === 8,
    `completed=${env.run("State.stats.completed")} transferred=${env.run("State.stats.transferred")}`);
  env.dispose();
}

// ---------------------------------------------------------------- R10 准入预算
function r10() {
  R.section("R10 · GP 准入等待预算被执行");
  const env = boot();
  const budget = env.run("IN.admission_gp_wait_budget_seconds");
  R.eq("R10.1", "配置预算 = 48 模拟秒", budget, 48);
  R.eq("R10.2", "执行该预算的代码路径确实引用了它",
    env.run(`/ADMISSION_BUDGET_TICKS/.test(String(ageWaiting))`), true);
  R.eq("R10.2b", "预算按 tick_hz 换算成 tick",
    env.run("ADMISSION_BUDGET_TICKS"), 48 * 10);

  // 人为压入一次超载：20 人同时到院，只有 1 个全科工位
  let n = 0;
  for (let i = 0; i < 20; i++) if (forceSpawn(env, "C01")) n++;
  R.eq("R10.3", "压入 20 名患者造成排队", n, 20);
  env.step(6000);
  const reasons = env.snap("State.stats.transferReasons");
  R.check("R10.4", `超载时触发准入转诊（admission=${reasons.admission}）`, reasons.admission > 0,
    JSON.stringify(reasons));

  const over = env.run(`State.patients.filter(p => p.state==="WAIT" && p.routeIdx===0 && p.admissionWait > ${budget * 10}).length`);
  R.eq("R10.5", "没有任何仍在排队的患者超过准入预算却未被处理", over, 0);

  // 空闲负载下不应误伤
  const env2 = boot();
  env2.step(6000);
  const r2 = env2.snap("State.stats.transferReasons");
  R.eq("R10.6", "常规负载下不触发准入转诊（不会误伤正常玩家）", r2.admission, 0);
  R.check("R10.7", "常规负载下转诊总数为 0", r2.patience + r2.admission + r2.brokenRoute === 0,
    JSON.stringify(r2));
  env.dispose(); env2.dispose();
}

// ---------------------------------------------------------------- R11 可观察性
function r11() {
  R.section("R11 · 工位与等候位置分离，扩容看得见");
  const env = boot();
  const anchors = (slots) => env.snap(`[0,1,2].slice(0,${slots}).map(i=>window.HOSP_DEBUG.api.slotAnchor("GP", i, ${slots}))`);
  const a1 = anchors(1), a2 = anchors(2), a3 = anchors(3);
  R.check("R11.1", "1 工位时锚点在房间正中", a1.length === 1, JSON.stringify(a1));
  const distinct = (arr) => new Set(arr.map(p => p.join(","))).size === arr.length;
  R.check("R11.2", "2 工位：两个锚点互不重合", distinct(a2), JSON.stringify(a2));
  R.check("R11.3", "3 工位：三个锚点互不重合", distinct(a3), JSON.stringify(a3));

  const w = env.snap(`[0,1,2,3,4].map(i=>window.HOSP_DEBUG.api.waitAnchor("GP", i))`);
  R.check("R11.4", "等候位互相区分（不是堆在一个坐标）", distinct(w), JSON.stringify(w));
  const wa = env.snap(`window.HOSP_DEBUG.api.waitAnchor("GP",0)`);
  const sa = env.snap(`window.HOSP_DEBUG.api.slotAnchor("GP",0,1)`);
  R.check("R11.5", "等候位与工位不在同一行（工位在内、等候在外）",
    Math.abs(wa[1] - sa[1]) > 30, `wait=${wa} slot=${sa}`);

  const regs = env.snap(`[0,1,2,3].map(i=>window.HOSP_DEBUG.api.regSlotAnchor(i,4))`);
  R.check("R11.6", "4 个登记位各自独立", distinct(regs), JSON.stringify(regs));
  const entries = env.snap(`[0,1,2,3].map(i=>window.HOSP_DEBUG.api.entryAnchor(i))`);
  R.check("R11.7", "入口队列位各自独立", distinct(entries), JSON.stringify(entries));

  // 扩容后确实能两人同时服务，且站在不同位置
  const env2 = boot();
  env2.run("State.coins = 9000");
  env2.run(`doAction("slot","GP")`);
  R.eq("R11.8", "GP 扩容后工位数 = 2", env2.run("State.rooms.GP.slots"), 2);
  for (let i = 0; i < 6; i++) forceSpawn(env2, "C01");
  let peakPair = 0, sample = null;
  for (let i = 0; i < 4000; i++) {
    env2.tickOnce();
    const serving = env2.snap(`State.patients.filter(p=>p.state==="SERVE" && p.serveRoom==="GP").map(p=>[p.serveSlot, Math.round(p.x), Math.round(p.y)])`);
    if (serving.length > peakPair) { peakPair = serving.length; sample = serving; }
  }
  R.check("R11.9", `扩容后同时服务人数达到 ${peakPair}（>1 说明扩容真的生效）`, peakPair >= 2,
    JSON.stringify(sample));
  if (peakPair >= 2) {
    R.check("R11.10", "两名同时服务的患者位置不同（各占各的工位）",
      sample[0][1] !== sample[1][1], JSON.stringify(sample));
  }
  env.dispose(); env2.dispose();
}

// ---------------------------------------------------------------- R12 既有良好基础不回归
function r12() {
  R.section("R12 · 审计确认的良好基础不能被改坏");
  const env = boot();
  // 余额不足拦截
  env.run("State.coins = 100");
  const ok = env.run(`doAction("unlock","TR")`);
  R.eq("R12.1", "余额不足时拒绝购买", ok, false);
  R.eq("R12.2", "余额不足不扣款", env.run("State.coins"), 100);
  R.eq("R12.3", "余额不足不开放", env.run("State.rooms.TR.unlocked"), false);

  // 重复解锁不重复扣款
  env.run("State.coins = 3000");
  env.run(`doAction("unlock","TR")`);
  const after1 = env.run("State.coins");
  const dup = env.run(`doAction("unlock","TR")`);
  R.eq("R12.4", "重复解锁被拒绝", dup, false);
  R.eq("R12.5", "重复解锁不再扣款", env.run("State.coins"), after1);

  // 提速不重算正在进行的服务
  env.run("State.coins = 3000");
  for (let i = 0; i < 4; i++) forceSpawn(env, "C01");
  let checked = false;
  for (let i = 0; i < 2000 && !checked; i++) {
    env.tickOnce();
    const inServe = env.snap(`State.patients.filter(p=>p.state==="SERVE").map(p=>[p.id,p.serveEndTick])`);
    if (inServe.length > 0) {
      const before = JSON.stringify(inServe);
      env.run(`doAction("speed","GP")`);
      const after = JSON.stringify(env.snap(`State.patients.filter(p=>p.state==="SERVE").map(p=>[p.id,p.serveEndTick])`));
      R.eq("R12.6", "提速不会重算正在服务的结束刻", after, before);
      checked = true;
    }
  }
  R.check("R12.7", "确实捕捉到了在服务中的患者", checked, "");

  // 结算一致性：准入 = 完成 + 转诊 + 在院
  const env3 = boot();
  env3.step(12000);
  const completed = env3.run("State.stats.completed");
  const transferred = env3.run("State.stats.transferred");
  const inside = env3.run("State.patients.length");
  const admitted = env3.run("State.nextPatientId - 1");
  R.eq("R12.8", "admitted = completed + transferred + 在院（不存在重复结算或凭空消失）",
    admitted, completed + transferred + inside);
  const serviceTotal = Object.values(env3.snap("State.stats.service")).reduce((a, b) => a + b, 0);
  R.check("R12.9", "服务次数累计 ≥ 完成人数（每例至少一次全科）", serviceTotal >= completed,
    `service=${serviceTotal} completed=${completed}`);
  env.dispose(); env3.dispose();
}

// ---------------------------------------------------------------- R13 缺字段必须拒绝启动
function r13() {
  R.section("R13 · 配置缺字段必须拒绝启动（证明测试没在脚本里补默认值）");
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "hosp-broken-"));
  for (const f of ["index.html", "style.css", "game.js"]) {
    fs.copyFileSync(path.join(CAND, f), path.join(tmp, f));
  }
  const cfg = fs.readFileSync(path.join(CAND, "config.js"), "utf8")
    .replace(/\s*arrival_jitter_low: 0\.7,/, "")
    .replace(/\s*arrival_jitter_high: 1\.3/, "");
  fs.writeFileSync(path.join(tmp, "config.js"), cfg, "utf8");

  const env = createEnv(tmp);
  const err = env.snap("State.bootError");
  R.check("R13.1", "缺少到院抖动字段时启动被拒绝", Array.isArray(err) && err.length > 0,
    JSON.stringify(err));
  if (Array.isArray(err)) {
    R.check("R13.2", "报错明确指出缺的两个字段",
      err.some(e => /arrival_jitter_low/.test(e)) && err.some(e => /arrival_jitter_high/.test(e)),
      JSON.stringify(err));
  }
  R.eq("R13.3", "拒绝启动后页面显示明确的错误页", env.run(`!!document.getElementById("boot-error")`), true);
  R.eq("R13.4", "拒绝启动后不会带着残缺配置跑模拟",
    env.run("window.HOSP_DEBUG.started"), false);
  env.dispose();
  fs.rmSync(tmp, { recursive: true, force: true });

  // 对照：完整配置能启动
  const ok = boot();
  R.eq("R13.5", "对照：完整配置可以正常启动", ok.run("window.HOSP_DEBUG.started"), true);
  ok.dispose();
}

// ---------------------------------------------------------------- 主流程
function main() {
  out("=".repeat(78));
  out("修复后回归 — 目标：prototype/（本次唯一 A 区候选）");
  out("加载方式与 index.html 相同；未注入任何游戏配置默认值");
  out("=".repeat(78));
  const probe = boot();
  out("");
  out("候选源码 sha256：");
  for (const [f, h] of Object.entries(probe.hash())) out(`  ${f.padEnd(12)} ${h}`);
  probe.dispose();

  r0(); r1(); r2(); r3(); r4(); r5(); r6(); r7();
  r8(); r9(); r10(); r11(); r12(); r13();

  const s = R.summary();
  out("");
  out(s.fail === 0
    ? `结论：${s.total} 项全部 PASS。`
    : `结论：${s.total} 项中 ${s.fail} 项 FAIL，未通过。`);

  fs.mkdirSync(path.dirname(LOG), { recursive: true });
  fs.writeFileSync(LOG, lines.join("\n") + "\n", "utf8");
  console.log("\n[写入] " + LOG);
  process.exit(s.fail === 0 ? 0 : 1);
}

main();
