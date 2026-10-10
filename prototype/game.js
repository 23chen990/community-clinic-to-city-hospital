// ============================================================
// 晴天小医院 · A 区候选 — 游戏主体
// 唯一版本：见 CFG.version / CFG.candidate（与 config.js、VERSION.txt 同版）
// 架构：模拟（10Hz tick）与渲染（rAF）分离；配置全部来自 window.HOSP_CONFIG
//
// 本轮修复（对应独立验收 v0.1）：
//   P0-1 到院断流        —— 补齐抖动输入 + 启动校验 + 显式 nextArrivalTick
//   P0-2 重复开业补助    —— 开放科室不再发放，T01 为唯一口径
//   P0-3 末站倍率未接入  —— last_multiplier 作用于路线最后一站
//   P1-1 保存静默失败    —— 事务化：写盘失败回滚内存并提示；版本校验；坏档备份
//   P1-2 面板/后台不暂停 —— 面板打开即暂停；页面隐藏不补跑；单帧 dt 上限
//   P1-3 面板与按钮不刷新—— 每帧同步暂停/倍速/存档状态与按钮可用性
//   P1-4 横屏面板越界    —— canvas 等比 contain + 面板响应式不越界 + 可读性下限
//   P2   可观察性        —— 工位与等候位置分离，扩容肉眼可见
//   P2   登记并发/准入   —— registration_slots 与 admission_gp_wait_budget 实际执行
// ============================================================
"use strict";

const CFG = window.HOSP_CONFIG;
const IN = CFG.inputs;

// ---------- 启动校验：缺字段必须报错，绝不允许静默 NaN ----------
function validateConfig() {
  const problems = [];
  const num = (v) => typeof v === "number" && Number.isFinite(v);
  const needInputs = [
    "tick_hz", "initial_coins", "opening_grant", "speed_gain_per_level",
    "patience_seconds", "tutorial_grace_seconds", "max_patients",
    "walk_units_per_second", "registration_seconds", "registration_slots",
    "settle_seconds", "autosave_sim_seconds", "admission_gp_wait_budget_seconds",
    "arrival_jitter_low", "arrival_jitter_high",
  ];
  if (!CFG || typeof CFG !== "object") return ["window.HOSP_CONFIG 不存在"];
  if (!IN || typeof IN !== "object") return ["HOSP_CONFIG.inputs 不存在"];
  for (const k of needInputs) if (!num(IN[k])) problems.push(`inputs.${k} 缺失或非有限数值`);
  if (!num(CFG.arrival_per_minute) || CFG.arrival_per_minute <= 0) problems.push("arrival_per_minute 缺失或非正数");
  if (num(IN.arrival_jitter_low) && num(IN.arrival_jitter_high) && IN.arrival_jitter_low > IN.arrival_jitter_high) {
    problems.push("arrival_jitter_low 大于 arrival_jitter_high");
  }
  if (num(IN.registration_slots) && IN.registration_slots < 1) problems.push("registration_slots 必须 ≥ 1");
  if (!CFG.rooms || !CFG.layout || !Array.isArray(CFG.cases) || !Array.isArray(CFG.tasks)) {
    problems.push("rooms / layout / cases / tasks 结构缺失");
  }
  return problems;
}

const TICK_HZ = IN.tick_hz;                       // 10 tick / 模拟秒
const PATIENCE_TICKS = Math.round(IN.patience_seconds * TICK_HZ);
const GRACE_TICKS = Math.round(IN.tutorial_grace_seconds * TICK_HZ);
const REG_TICKS = Math.round(IN.registration_seconds * TICK_HZ);
const SETTLE_TICKS = Math.round(IN.settle_seconds * TICK_HZ);
const WALK_PER_TICK = IN.walk_units_per_second / TICK_HZ;
const AUTOSAVE_EVERY = Math.round(IN.autosave_sim_seconds * TICK_HZ);
const ARRIVAL_INTERVAL = Math.round((60 / CFG.arrival_per_minute) * TICK_HZ); // 平均到院间隔（tick）
const ADMISSION_BUDGET_TICKS = Math.round(IN.admission_gp_wait_budget_seconds * TICK_HZ);
const MAX_FRAME_DT = 0.25;                        // 单帧最多推进 0.25 真实秒，禁止后台补跑
const SAVE_KEY = "sunny_hospital_save_v1";
const SAVE_VERSION = 2;                           // v1（上传版）含 NaN 到院字段，不再接受

// 服务时长（tick）= base_ticks / (1 + level * speed_gain) × 病例末站倍率
function serviceTicks(room, level, multiplier) {
  const m = (typeof multiplier === "number" && Number.isFinite(multiplier) && multiplier > 0) ? multiplier : 1;
  const ticks = room.base_ticks / (1 + level * IN.speed_gain_per_level) * m;
  return Math.max(1, Math.round(ticks));
}

// ---------- 全局状态 ----------
const State = {
  coins: IN.initial_coins,
  simTick: 0,
  pausedByUser: false,
  pausedByPanel: false,
  hidden: false,
  speed: 1,                       // 1 / 2 / 4
  rooms: {},
  patients: [],
  nextPatientId: 1,
  stats: {
    completed: 0, transferred: 0, investments: 0,
    service: {}, claimedTasks: {}, grantLog: [],
    transferReasons: { patience: 0, admission: 0, brokenRoute: 0 },
  },
  nextArrivalTick: 0,             // 显式到院时刻，替代原来的"回改 lastArrivalTick"写法
  lastSaveTick: 0,
  rngState: 12345,
  selectedRoom: null,
  regSlots: [],                   // 登记位占用（patientId | null），长度 = registration_slots
  regQueue: [],                   // 入口队列（patientId 顺序）
  saveStatus: "ok",               // "ok" | "failed"
  saveError: "",
  bootError: null,
};

// 兼容旧字段名：State.paused 现在由两个来源合成
Object.defineProperty(State, "paused", {
  get() { return State.pausedByUser || State.pausedByPanel; },
  enumerable: false,
});
function isPaused() { return State.pausedByUser || State.pausedByPanel; }

function initRooms() {
  State.rooms = {};
  for (const id in CFG.rooms) {
    const r = CFG.rooms[id];
    State.rooms[id] = {
      unlocked: !!r.initial,
      level: 0,
      slots: r.initial ? r.initial_slots : 0,
      waiting: [],
      slotBusy: new Array(r.initial ? r.initial_slots : 0).fill(null),
    };
  }
}
function initQueues() {
  State.regSlots = new Array(Math.max(1, IN.registration_slots | 0)).fill(null);
  State.regQueue = [];
}

// ---------- 简单确定性 RNG（可存档恢复） ----------
function rng() {
  State.rngState = (State.rngState * 1103515245 + 12345) & 0x7fffffff;
  return State.rngState / 0x7fffffff;
}
function jitter() {
  const lo = IN.arrival_jitter_low, hi = IN.arrival_jitter_high;
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return 1;   // 兜底：绝不再产生 NaN
  return lo + rng() * (hi - lo);
}
function nextArrivalDelay() {
  return Math.max(1, Math.round(ARRIVAL_INTERVAL * jitter()));
}

// ---------- 视口 / 布局锚点 ----------
const LAYOUT = CFG.layout;
const ROOM_W = LAYOUT.room_size.w;
const ROOM_H = LAYOUT.room_size.h;

const view = { scale: 1, cssW: 0, cssH: 0 };
const MIN_CSS = { title: 14, body: 10.5, small: 9, patientR: 5.5 };

// 设计单位 → 保证在屏幕上的最小可读尺寸
function designPx(basePx, minCssPx) {
  const need = minCssPx / Math.max(view.scale, 0.01);
  return Math.min(Math.max(basePx, need), basePx * 1.9);
}
function patientR() {
  return Math.min(14, Math.max(8, MIN_CSS.patientR / Math.max(view.scale, 0.01)));
}

function roomRect(rid) {
  const n = LAYOUT.nodes[rid];
  return { x: n[0] - ROOM_W / 2, y: n[1] - ROOM_H / 2, w: ROOM_W, h: ROOM_H };
}
function roomCenter(rid) { const n = LAYOUT.nodes[rid]; return [n[0], n[1]]; }
function dist(ax, ay, bx, by) { return Math.hypot(ax - bx, ay - by); }

// 每个服务位一个独立锚点：扩容后新增的桌子看得见
function slotAnchor(rid, i, slots) {
  const rc = roomRect(rid);
  const y = rc.y + ROOM_H - 28;
  if (slots <= 1) return [rc.x + rc.w / 2, y];
  const usable = rc.w - 60;
  return [rc.x + 30 + (usable / (slots - 1)) * i, y];
}
// 等候位：排在房间朝向走廊的一侧，按队位分开，不再堆在一个点
function waitAnchor(rid, idx) {
  const rc = roomRect(rid);
  const topSide = (rc.y + rc.h / 2) < LAYOUT.corridor_y;   // GP / PH 在上排
  const baseY = topSide ? rc.y + rc.h + 22 : rc.y - 22;
  const perRow = 5, stepX = 30, stepY = 30, maxIdx = perRow * 3 - 1;
  const k = Math.min(idx, maxIdx);
  const row = Math.floor(k / perRow), col = k % perRow;
  const x = rc.x + 30 + col * stepX + (row % 2) * 15;
  const y = topSide ? baseY + row * stepY : baseY - row * stepY;
  return [x, y];
}
// 登记位：登记台旁的竖直一排
function regSlotAnchor(i, n) {
  const rg = LAYOUT.nodes.RG;
  const gap = 30;
  return [rg[0], rg[1] + (i - (n - 1) / 2) * gap];
}
// 入口队列：在入口上方竖排
function entryAnchor(idx) {
  const e = LAYOUT.nodes.ENTRY;
  return [e[0] + 30, LAYOUT.corridor_y - 34 - Math.min(idx, 6) * 30];
}
function byId(pid) { return State.patients.find(x => x.id === pid) || null; }

// ---------- 病例生成 ----------
function pickCase() {
  const w = CFG.case_weights;
  const total = w.base + w.treatment + w.lab;
  let r = rng() * total;
  let group;
  if ((r -= w.base) < 0) group = "base";
  else if ((r -= w.treatment) < 0) group = "treatment";
  else group = "lab";
  const candidates = CFG.cases.filter(c => c.group === group);
  return candidates[Math.floor(rng() * candidates.length)];
}
function routeAvailable(route) {
  return route.every(rid => State.rooms[rid] && State.rooms[rid].unlocked);
}

// ---------- 患者 ----------
function spawnPatient() {
  if (State.patients.length >= IN.max_patients) return null;
  let cs = null;
  for (let i = 0; i < 5; i++) {
    const c = pickCase();
    if (c && routeAvailable(c.route)) { cs = c; break; }
  }
  if (!cs) cs = CFG.cases.find(c => c.group === "base" && routeAvailable(c.route));
  if (!cs) return null;

  const entry = LAYOUT.nodes.ENTRY;
  const p = {
    id: State.nextPatientId++,
    caseId: cs.id,
    route: cs.route.slice(),
    lastMultiplier: cs.last_multiplier,
    routeIdx: -1,
    reward: Math.round(cs.reward / IN.reward_round) * IN.reward_round,
    state: "QUEUE",                          // 入口队列中
    pending: "ENTRY",
    arrived: false,
    x: entry[0], y: entry[1],
    tx: entry[0], ty: entry[1],
    patienceLeft: PATIENCE_TICKS,
    admissionWait: 0,
    serveRoom: null, serveSlot: -1, serveEndTick: 0,
    regSlot: -1, regEndTick: 0,
    settleEndTick: 0,
  };
  State.patients.push(p);
  State.regQueue.push(p.id);
  const [x, y] = entryAnchor(State.regQueue.length - 1);
  p.tx = x; p.ty = y;
  return p;
}

// ---------- 模拟 tick ----------
function tick() {
  State.simTick++;
  finishTimers();
  retargetQueues();
  dispatchRooms();
  dispatchRegistration();
  movePatients();
  arrivePatients();
  ageWaiting();
  cleanupPatients();

  if (State.simTick - State.lastSaveTick >= AUTOSAVE_EVERY) {
    State.lastSaveTick = State.simTick;
    saveGame(true);                          // 自动存档失败会置 saveStatus=failed 并在 HUD 显示
  }
  checkTasks();
}

// 1) 登记 / 服务 / 结算 计时结束
function finishTimers() {
  for (const p of State.patients) {
    if (p.state === "REGISTER" && State.simTick >= p.regEndTick) {
      if (p.regSlot >= 0 && State.regSlots[p.regSlot] === p.id) State.regSlots[p.regSlot] = null;
      p.regSlot = -1;
      p.routeIdx = 0;
      p.state = "WALK";
      p.pending = "ROOM";
      const [tx, ty] = waitAnchor(p.route[0], 0);
      p.tx = tx; p.ty = ty; p.arrived = false;
    } else if (p.state === "SERVE" && State.simTick >= p.serveEndTick) {
      const rs = State.rooms[p.serveRoom];
      if (rs && p.serveSlot >= 0) rs.slotBusy[p.serveSlot] = null;
      State.stats.service[p.serveRoom] = (State.stats.service[p.serveRoom] || 0) + 1;
      const rid = p.serveRoom;
      p.serveRoom = null; p.serveSlot = -1;
      // 离开全科后不再受准入预算约束
      if (rid === "GP") p.admissionWait = 0;
      p.routeIdx++;
      if (p.routeIdx < p.route.length) {
        p.state = "WALK";
        p.pending = "ROOM";
        const [tx, ty] = waitAnchor(p.route[p.routeIdx], 0);
        p.tx = tx; p.ty = ty; p.arrived = false;
      } else {
        p.state = "LEAVE";
        p.pending = "EXIT";
        p.tx = LAYOUT.nodes.EXIT[0]; p.ty = LAYOUT.nodes.EXIT[1]; p.arrived = false;
      }
    } else if (p.state === "SETTLE" && State.simTick >= p.settleEndTick) {
      State.coins += p.reward;
      State.stats.completed++;
      p.state = "DONE";
    }
  }
}

// 2) 队伍位置重算：等候与入口队列里的人各自走到自己的位子
function retargetQueues() {
  State.regQueue.forEach((pid, i) => {
    const p = byId(pid);
    if (!p || p.pending !== "ENTRY") return;
    const [x, y] = entryAnchor(i);
    if (p.tx !== x || p.ty !== y) { p.tx = x; p.ty = y; p.arrived = false; }
  });
  for (const rid in State.rooms) {
    const rs = State.rooms[rid];
    rs.waiting.forEach((pid, i) => {
      const p = byId(pid);
      if (!p || p.state !== "WAIT" || p.pending !== null) return;
      const [x, y] = waitAnchor(rid, i);
      if (p.tx !== x || p.ty !== y) { p.tx = x; p.ty = y; p.arrived = false; }
    });
  }
}

// 3) 房间调度：队首 → 空闲服务位（先预约，走到位再开始计时）
function dispatchRooms() {
  for (const rid in State.rooms) {
    const rs = State.rooms[rid];
    if (!rs.unlocked) continue;
    while (rs.slotBusy.length < rs.slots) rs.slotBusy.push(null);
    if (rs.slotBusy.length > rs.slots) rs.slotBusy.length = rs.slots;
    for (let s = 0; s < rs.slots; s++) {
      if (rs.slotBusy[s] !== null || rs.waiting.length === 0) continue;
      const pid = rs.waiting[0];
      const p = byId(pid);
      if (!p || p.state === "DONE" || p.state === "GONE") { rs.waiting.shift(); s--; continue; }
      // FIFO：队首还没准备好（比如正在走到位）就不越位，也不丢弃
      if (p.state !== "WAIT" || p.pending !== null) break;
      const isLast = (p.routeIdx === p.route.length - 1);
      p.willBeLastStation = isLast;
      rs.waiting.shift();
      rs.slotBusy[s] = { patientId: pid, endTick: 0, reserved: true };
      p.serveRoom = rid; p.serveSlot = s;
      p.state = "APPROACH"; p.pending = "SERVE";
      const [x, y] = slotAnchor(rid, s, rs.slots);
      p.tx = x; p.ty = y; p.arrived = false;
    }
  }
}

// 4) 登记调度：入口队首 → 空闲登记位（严格执行 registration_slots 并发上限）
function dispatchRegistration() {
  const n = State.regSlots.length;
  for (let s = 0; s < n; s++) {
    if (State.regSlots[s] !== null || State.regQueue.length === 0) continue;
    const pid = State.regQueue[0];
    const p = byId(pid);
    if (!p) { State.regQueue.shift(); s--; continue; }
    if (!p.arrived) break;                     // FIFO：队首还没走到就不越位
    State.regQueue.shift();
    State.regSlots[s] = pid;
    p.regSlot = s;
    p.pending = "REG";
    p.state = "APPROACH";
    const [x, y] = regSlotAnchor(s, n);
    p.tx = x; p.ty = y; p.arrived = false;
  }
}

// 5) 移动
function movePatients() {
  for (const p of State.patients) {
    if (p.state === "REGISTER" || p.state === "SERVE" || p.state === "SETTLE" ||
        p.state === "DONE" || p.state === "GONE") continue;
    const dx = p.tx - p.x, dy = p.ty - p.y;
    const d = Math.hypot(dx, dy);
    if (d <= WALK_PER_TICK) {
      p.x = p.tx; p.y = p.ty;
      if (!p.arrived) { p.arrived = true; onArrive(p); }
    } else {
      p.x += dx / d * WALK_PER_TICK;
      p.y += dy / d * WALK_PER_TICK;
      p.arrived = false;
    }
  }
}

function onArrive(p) {
  if (p.pending === "ENTRY") { p.state = "QUEUE"; return; }
  if (p.pending === "REG") {
    p.state = "REGISTER";
    p.pending = null;
    p.regEndTick = State.simTick + REG_TICKS;
    return;
  }
  if (p.pending === "ROOM") {
    const rid = p.route[p.routeIdx];
    const rs = State.rooms[rid];
    if (!rs || !rs.unlocked) { transfer(p, "brokenRoute"); return; }
    rs.waiting.push(p.id);
    p.pending = null;
    p.state = "WAIT";
    const [x, y] = waitAnchor(rid, rs.waiting.length - 1);
    p.tx = x; p.ty = y; p.arrived = false;
    return;
  }
  if (p.pending === "SERVE") {
    const rid = p.serveRoom, rs = State.rooms[rid];
    const mult = p.willBeLastStation ? p.lastMultiplier : 1;
    p.serveEndTick = State.simTick + serviceTicks(CFG.rooms[rid], rs.level, mult);
    rs.slotBusy[p.serveSlot] = { patientId: p.id, endTick: p.serveEndTick, reserved: false };
    p.state = "SERVE"; p.pending = null;
    if (rid === "GP") p.admissionWait = 0;
    return;
  }
  if (p.pending === "EXIT") {
    p.state = "SETTLE";
    p.pending = null;
    p.settleEndTick = State.simTick + SETTLE_TICKS;
    return;
  }
}

// 6) 新患者到院：显式 nextArrivalTick，任何情况下都必须保持有限值
function arrivePatients() {
  if (!Number.isFinite(State.nextArrivalTick)) State.nextArrivalTick = State.simTick + nextArrivalDelay();
  if (State.simTick >= State.nextArrivalTick) {
    spawnPatient();
    State.nextArrivalTick = State.simTick + nextArrivalDelay();
  }
}

// 7) 耐心 / 准入预算
function transfer(p, reason) {
  const rid = p.route[p.routeIdx];
  const rs = State.rooms[rid];
  if (rs) {
    const qi = rs.waiting.indexOf(p.id);
    if (qi >= 0) rs.waiting.splice(qi, 1);
  }
  const qi2 = State.regQueue.indexOf(p.id);
  if (qi2 >= 0) State.regQueue.splice(qi2, 1);
  if (p.regSlot >= 0 && State.regSlots[p.regSlot] === p.id) State.regSlots[p.regSlot] = null;
  State.stats.transferred++;
  State.stats.transferReasons[reason] = (State.stats.transferReasons[reason] || 0) + 1;
  p.state = "GONE";
  p.pending = null;
}

function ageWaiting() {
  const inGrace = State.simTick < GRACE_TICKS;
  for (let i = State.patients.length - 1; i >= 0; i--) {
    const p = State.patients[i];
    if (p.state !== "WAIT" && p.state !== "QUEUE") continue;
    p.patienceLeft--;
    // 准入等待：入口队列 + 第一个科室（GP）的排队时间
    const atFirstRoom = (p.state === "WAIT" && p.routeIdx === 0);
    if (p.state === "QUEUE" || atFirstRoom) p.admissionWait++;
    if (inGrace) continue;
    if (p.patienceLeft <= 0) { transfer(p, "patience"); continue; }
    if (p.admissionWait > ADMISSION_BUDGET_TICKS) { transfer(p, "admission"); continue; }
  }
}

function cleanupPatients() {
  for (let i = State.patients.length - 1; i >= 0; i--) {
    const st = State.patients[i].state;
    if (st === "DONE" || st === "GONE") State.patients.splice(i, 1);
  }
}

// ---------- 渲染 ----------
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");

function fitCanvas() {
  // 等比 contain：canvas 既不被面板挤出，也不会被拉伸变形
  const box = document.getElementById("stage-view") || document.getElementById("stage");
  if (!box) return;
  const availW = box.clientWidth, availH = box.clientHeight;
  if (!(availW > 0) || !(availH > 0)) return;
  const scale = Math.min(availW / LAYOUT.area_width, availH / LAYOUT.area_height);
  // 先取整高度，再按设计比例反推宽度，避免整数取整造成哪怕 0.3% 的纵向拉伸
  let cssH = Math.max(1, Math.floor(LAYOUT.area_height * scale));
  let cssW = Math.max(1, Math.round(cssH * (LAYOUT.area_width / LAYOUT.area_height)));
  if (cssW > availW) { cssW = availW; cssH = Math.max(1, Math.round(cssW * (LAYOUT.area_height / LAYOUT.area_width))); }
  canvas.style.width = cssW + "px";
  canvas.style.height = cssH + "px";
  view.scale = cssW / LAYOUT.area_width;
  view.cssW = cssW; view.cssH = cssH;
}

function screenToCanvas(clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  if (!rect.width || !rect.height) return [0, 0];
  return [
    (clientX - rect.left) / rect.width * canvas.width,
    (clientY - rect.top) / rect.height * canvas.height,
  ];
}

function roundRect(x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

function drawRoom(rid) {
  const r = CFG.rooms[rid];
  const rs = State.rooms[rid];
  const rect = roomRect(rid);
  const selected = State.selectedRoom === rid;

  if (!rs.unlocked) {
    ctx.fillStyle = "#9aa8b4"; ctx.strokeStyle = "#6b7884";
  } else {
    ctx.fillStyle = selected ? "#fff4d6" : "#eaf3f4";
    ctx.strokeStyle = selected ? "#d97706" : "#5a8a9a";
  }
  ctx.lineWidth = selected ? 4 : 2;
  roundRect(rect.x, rect.y, rect.w, rect.h, 14);
  ctx.fill(); ctx.stroke();

  ctx.textAlign = "center";
  const fTitle = designPx(22, MIN_CSS.title);
  const fBody = designPx(15, MIN_CSS.body);

  ctx.fillStyle = rs.unlocked ? "#135f68" : "#5a6b7c";
  ctx.font = `bold ${Math.round(fTitle)}px system-ui`;
  ctx.fillText(r.name, rect.x + rect.w / 2, rect.y + 34);

  if (!rs.unlocked) {
    ctx.font = `${Math.round(fBody)}px system-ui`;
    ctx.fillStyle = "#5a6b7c";
    ctx.fillText("未开放", rect.x + rect.w / 2, rect.y + 66);
    if (r.unlock_cost > 0) {
      ctx.fillStyle = "#d97706";
      ctx.font = `bold ${Math.round(fBody)}px system-ui`;
      ctx.fillText(`投入 ${r.unlock_cost} 金币开放`, rect.x + rect.w / 2, rect.y + 96);
    }
    ctx.strokeStyle = "rgba(255,255,255,.35)";
    ctx.lineWidth = 1;
    for (let i = -rect.h; i < rect.w; i += 12) {
      ctx.beginPath();
      ctx.moveTo(rect.x + i, rect.y + rect.h);
      ctx.lineTo(rect.x + i + rect.h, rect.y);
      ctx.stroke();
    }
    return;
  }

  const busy = rs.slotBusy.filter(s => s).length;
  ctx.font = `${Math.round(fBody)}px system-ui`;
  ctx.fillStyle = "#1a8a9e";
  ctx.fillText(`工位 ${busy}/${rs.slots}`, rect.x + rect.w / 2, rect.y + 66);
  ctx.fillStyle = rs.waiting.length > 0 ? "#d97706" : "#5a8a9e";
  ctx.fillText(`等候 ${rs.waiting.length} 人`, rect.x + rect.w / 2, rect.y + 94);
  if (rs.level > 0) {
    ctx.fillStyle = "#7a8a99";
    ctx.font = `${Math.round(designPx(12, MIN_CSS.small))}px system-ui`;
    ctx.fillText(`提速 Lv${rs.level}`, rect.x + rect.w / 2, rect.y + 118);
  }

  // 工位标记：每个服务位一张桌子，扩容后新增的桌子肉眼可见
  const pr = patientR();
  for (let s = 0; s < rs.slots; s++) {
    const [cx, cy] = slotAnchor(rid, s, rs.slots);
    const occ = rs.slotBusy[s];
    ctx.fillStyle = occ ? "rgba(42,158,106,.20)" : "rgba(26,138,158,.12)";
    ctx.strokeStyle = occ ? "#2a9e6a" : "#7fa8b4";
    ctx.lineWidth = 2;
    roundRect(cx - pr * 1.5, cy - pr * 1.1, pr * 3, pr * 2.2, 6);
    ctx.fill(); ctx.stroke();
  }
}

function drawCorridor() {
  ctx.strokeStyle = "#b8c8d2";
  ctx.lineWidth = 6;
  ctx.beginPath();
  ctx.moveTo(LAYOUT.nodes.ENTRY[0], LAYOUT.corridor_y);
  ctx.lineTo(LAYOUT.nodes.EXIT[0], LAYOUT.corridor_y);
  ctx.stroke();

  ctx.textAlign = "center";
  ctx.font = `bold ${Math.round(designPx(16, MIN_CSS.body))}px system-ui`;
  ctx.fillStyle = "#2a9e6a";
  ctx.fillText("入口", LAYOUT.nodes.ENTRY[0], LAYOUT.corridor_y + 34);
  ctx.fillStyle = "#9e4a2a";
  ctx.fillText("出口", LAYOUT.nodes.EXIT[0], LAYOUT.corridor_y - 24);

  // 登记台 + 登记位（数量 = registration_slots）
  const n = State.regSlots.length;
  const rg = LAYOUT.nodes.RG;
  const pr = patientR();
  for (let i = 0; i < n; i++) {
    const [cx, cy] = regSlotAnchor(i, n);
    const occ = State.regSlots[i] !== null;
    ctx.fillStyle = occ ? "#f4d6a0" : "#fdf3e0";
    ctx.strokeStyle = occ ? "#c89a4a" : "#e0cfa8";
    ctx.lineWidth = 2;
    roundRect(cx - 26, cy - pr - 4, 52, (pr + 4) * 2, 6);
    ctx.fill(); ctx.stroke();
  }
  ctx.fillStyle = "#7a5a1a";
  ctx.font = `bold ${Math.round(designPx(14, MIN_CSS.body))}px system-ui`;
  ctx.fillText("登记", rg[0] - 52, rg[1] + 5);

  ctx.fillStyle = "rgba(120,134,148,.55)";
  ctx.font = `bold ${Math.round(designPx(15, MIN_CSS.small))}px system-ui`;
  ctx.textAlign = "left";
  ctx.fillText("B区 / C区 · 后续内容（本期不可开放）", 900, 692);
}

const STATE_COLOR = {
  QUEUE: "#4a8adf", APPROACH: "#3f7fc7", REGISTER: "#9a6adf",
  WAIT: "#d97706", SERVE: "#2a9e6a", WALK: "#4a8adf",
  LEAVE: "#2a9e6a", SETTLE: "#2a9e6a",
};

function drawPatient(p) {
  const r = patientR();
  const color = STATE_COLOR[p.state] || "#4a8adf";
  ctx.fillStyle = color;
  ctx.strokeStyle = "#fff";
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
  ctx.fill(); ctx.stroke();

  if (p.state === "WAIT" || p.state === "QUEUE") {
    const ratio = Math.max(0, Math.min(1, p.patienceLeft / PATIENCE_TICKS));
    const w = r * 2.4, h = Math.max(3, r * 0.34);
    ctx.fillStyle = "rgba(0,0,0,.3)";
    ctx.fillRect(p.x - w / 2, p.y - r - h - 4, w, h);
    ctx.fillStyle = ratio > 0.4 ? "#2a9e6a" : ratio > 0.2 ? "#d97706" : "#d04040";
    ctx.fillRect(p.x - w / 2, p.y - r - h - 4, w * ratio, h);
  }
}

function render() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#c8d8e2";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.strokeStyle = "rgba(255,255,255,.25)";
  ctx.lineWidth = 1;
  for (let x = 0; x < canvas.width; x += 64) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, canvas.height); ctx.stroke(); }
  for (let y = 0; y < canvas.height; y += 64) { ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(canvas.width, y); ctx.stroke(); }

  drawCorridor();
  ["GP", "PH", "TR", "LB"].forEach(drawRoom);
  for (const p of State.patients) drawPatient(p);
}

// ---------- 主循环 ----------
let acc = 0;
let lastTime = (typeof performance !== "undefined" ? performance.now() : 0);
function loop(now) {
  const dtRaw = (now - lastTime) / 1000;
  lastTime = now;
  if (!isPaused() && !document.hidden) {
    // 单帧上限 + 非负：后台返回后不补跑历史时间
    const dt = Math.min(Math.max(Number.isFinite(dtRaw) ? dtRaw : 0, 0), MAX_FRAME_DT);
    acc += dt * State.speed;
    const tickSec = 1 / TICK_HZ;
    let guard = 0;
    while (acc >= tickSec && guard < TICK_HZ * MAX_FRAME_DT * 4) { tick(); acc -= tickSec; guard++; }
    if (acc > tickSec) acc = 0;              // 超出保护上限就丢弃，不累积到下一帧
  } else {
    acc = 0;
  }
  render();
  updateHUD();
  requestAnimationFrame(loop);
}

// ---------- HUD ----------
const hudEls = {};
const hudCache = {};
function setText(el, key, value) {
  if (!el) return;
  if (hudCache[key] === value) return;
  hudCache[key] = value;
  el.textContent = value;
}
function updateHUD() {
  const d = document;
  hudEls.coins = hudEls.coins || d.querySelector("#hud-coins b");
  hudEls.patients = hudEls.patients || d.querySelector("#hud-patients b");
  hudEls.completed = hudEls.completed || d.querySelector("#hud-completed b");
  hudEls.speed = hudEls.speed || d.getElementById("btn-speed");
  hudEls.pause = hudEls.pause || d.getElementById("btn-pause");
  hudEls.save = hudEls.save || d.getElementById("hud-save-value");
  hudEls.saveChip = hudEls.saveChip || d.getElementById("hud-save");
  setText(hudEls.coins, "coins", String(State.coins));
  setText(hudEls.patients, "patients", String(State.patients.length));
  setText(hudEls.completed, "completed", String(State.stats.completed));
  setText(hudEls.speed, "speed", State.speed + "×");                       // 修复：倍速文案跟随真实速度
  setText(hudEls.pause, "pause", isPaused() ? "▶ 继续" : "⏸ 暂停");        // 修复：暂停文案跟随真实状态
  const ok = State.saveStatus === "ok";
  setText(hudEls.save, "save", ok ? "正常" : "失败");
  if (hudEls.saveChip) {
    const cls = "hud-item" + (ok ? "" : " bad");
    if (hudEls.saveChip.className !== cls) hudEls.saveChip.className = cls;
  }
  refreshPanelAffordability();                                            // 修复：按钮可用性每帧刷新
}

// ---------- 房间面板 ----------
const panel = document.getElementById("room-panel");
const rpBody = document.getElementById("rp-body");
const rpTitle = document.getElementById("rp-title");
let panelRefs = { unlock: null, slot: null, speed: null };

function openRoomPanel(rid) {
  State.selectedRoom = rid;
  panel.classList.remove("hidden");
  State.pausedByPanel = true;                 // 修复：打开面板即暂停
  renderRoomPanel();
  fitCanvas();                                // 可用宽度变了，重算等比缩放
}
function closeRoomPanel() {
  State.selectedRoom = null;
  panel.classList.add("hidden");
  State.pausedByPanel = false;                // 修复：关闭面板恢复
  panelRefs = { unlock: null, slot: null, speed: null };
  fitCanvas();
}
document.getElementById("rp-close").addEventListener("click", closeRoomPanel);

function renderRoomPanel() {
  const rid = State.selectedRoom;
  if (!rid) return;
  const r = CFG.rooms[rid];
  const rs = State.rooms[rid];
  rpTitle.textContent = r.name;
  panelRefs = { unlock: null, slot: null, speed: null };

  if (r.locked) {
    rpBody.innerHTML = `
      <div class="rp-desc">${r.desc}</div>
      <div class="rp-locked-msg">该科室属于${r.area}区后续内容，本期原型暂不开放。</div>`;
    return;
  }

  // 房间明细（随金币刷新的是按钮可用性，随状态刷新的是这些数字）
  const busy = rs.slotBusy.filter(s => s).length;
  const statsHtml = `
    <div class="rp-stat"><span>状态</span><b>${rs.unlocked ? "已开放" : "未开放"}</b></div>
    <div class="rp-stat"><span>当前服务</span><b>${busy} / ${rs.slots} 工位</b></div>
    <div class="rp-stat"><span>等候人数</span><b>${rs.waiting.length} 人</b></div>
    <div class="rp-stat"><span>提速等级</span><b>${rs.level} / ${r.upgrade_costs.length}</b></div>
    <div class="rp-stat"><span>基础服务时长</span><b>${(serviceTicks(r, rs.level, 1) / TICK_HZ).toFixed(1)} 秒</b></div>`;

  let actionsHtml = "";
  if (!rs.unlocked) {
    actionsHtml = `
      <div class="rp-desc">${r.desc}</div>
      <div class="rp-section-title">开放科室</div>
      <button class="rp-btn unlock" data-act="unlock" data-rid="${rid}">
        <span class="rp-btn-title">投入金币开放</span>
        <span class="rp-btn-sub">费用 ${r.unlock_cost} 金币 · 含基础设备与人员 · 立即启用</span>
      </button>`;
  } else {
    const slotIdx = rs.slots - r.initial_slots;
    let slotBtn;
    if (slotIdx < r.slot_costs.length) {
      const cost = r.slot_costs[slotIdx];
      slotBtn = `
        <button class="rp-btn" data-act="slot" data-rid="${rid}">
          <span class="rp-btn-title">扩容：增加服务位</span>
          <span class="rp-btn-sub">${rs.slots} → ${rs.slots + 1} 工位 · 费用 ${cost} 金币</span>
        </button>`;
    } else {
      slotBtn = `<div class="rp-locked-msg">已达最大 ${r.max_slots} 工位</div>`;
    }
    const lvlIdx = rs.level;
    let lvlBtn;
    if (lvlIdx < r.upgrade_costs.length) {
      const cost = r.upgrade_costs[lvlIdx];
      const curSecs = (serviceTicks(r, rs.level, 1) / TICK_HZ).toFixed(1);
      const newSecs = (serviceTicks(r, rs.level + 1, 1) / TICK_HZ).toFixed(1);
      lvlBtn = `
        <button class="rp-btn" data-act="speed" data-rid="${rid}">
          <span class="rp-btn-title">提速：缩短服务时间</span>
          <span class="rp-btn-sub">Lv${rs.level} → Lv${rs.level + 1} · ${curSecs}秒 → ${newSecs}秒 · 费用 ${cost} 金币</span>
        </button>`;
    } else {
      lvlBtn = `<div class="rp-locked-msg">已达最高提速等级</div>`;
    }
    actionsHtml = `
      <div class="rp-desc">${r.desc}</div>
      ${statsHtml}
      <div class="rp-section-title">投资</div>
      ${slotBtn}
      ${lvlBtn}
      <div class="rp-note">末站倍率按病例生效，不显示在这里。</div>`;
  }
  rpBody.innerHTML = actionsHtml;

  panelRefs.unlock = rpBody.querySelector('button[data-act="unlock"]');
  panelRefs.slot = rpBody.querySelector('button[data-act="slot"]');
  panelRefs.speed = rpBody.querySelector('button[data-act="speed"]');
  rpBody.querySelectorAll("button[data-act]").forEach(btn => {
    btn.addEventListener("click", () => doAction(btn.dataset.act, btn.dataset.rid));
  });
  refreshPanelAffordability();
}

// 只改 disabled，不重建 DOM：玩家不会失去点击位置，状态也能实时刷新
function refreshPanelAffordability() {
  const rid = State.selectedRoom;
  if (!rid) return;
  const r = CFG.rooms[rid], rs = State.rooms[rid];
  if (!rs) return;
  if (!rs.unlocked) {
    if (panelRefs.unlock) panelRefs.unlock.disabled = State.coins < r.unlock_cost;
    return;
  }
  const slotIdx = rs.slots - r.initial_slots;
  if (panelRefs.slot) {
    panelRefs.slot.disabled = !(slotIdx < r.slot_costs.length && State.coins >= r.slot_costs[slotIdx]);
  }
  const lvlIdx = rs.level;
  if (panelRefs.speed) {
    panelRefs.speed.disabled = !(lvlIdx < r.upgrade_costs.length && State.coins >= r.upgrade_costs[lvlIdx]);
  }
}

// ---------- 玩家命令（事务化：写盘失败即回滚） ----------
function snapshotForCommand() {
  const rooms = {};
  for (const rid in State.rooms) {
    const rs = State.rooms[rid];
    rooms[rid] = { unlocked: rs.unlocked, level: rs.level, slots: rs.slots, waiting: rs.waiting.slice(), slotBusy: rs.slotBusy.slice() };
  }
  return JSON.stringify({
    coins: State.coins, stats: State.stats, rooms, regSlots: State.regSlots.slice(),
  });
}
function restoreForCommand(raw) {
  const d = JSON.parse(raw);
  State.coins = d.coins;
  State.stats = d.stats;
  State.regSlots = d.regSlots;
  for (const rid in d.rooms) Object.assign(State.rooms[rid], d.rooms[rid]);
  State.saveStatus = "failed";
}
// 执行一次玩家命令：先改内存 → 立刻写盘 → 写盘失败则整体回滚并明确提示
function doAction(act, rid) {
  const r = CFG.rooms[rid];
  const rs = State.rooms[rid];
  if (!r || !rs) return false;
  if (r.locked) { toast(`${r.area}区为后续内容，本期不可开放`, "err"); return false; }

  // 先做纯校验，避免无谓写盘
  if (act === "unlock") {
    if (rs.unlocked) { toast("已开放", "err"); return false; }
    if (!Number.isFinite(r.unlock_cost) || State.coins < r.unlock_cost) { toast("金币不足", "err"); return false; }
  } else if (act === "slot") {
    if (!rs.unlocked) { toast("尚未开放", "err"); return false; }
    const slotIdx = rs.slots - r.initial_slots;
    if (slotIdx >= r.slot_costs.length) { toast("已达最大工位", "err"); return false; }
    if (State.coins < r.slot_costs[slotIdx]) { toast("金币不足", "err"); return false; }
  } else if (act === "speed") {
    if (!rs.unlocked) { toast("尚未开放", "err"); return false; }
    if (rs.level >= r.upgrade_costs.length) { toast("已达最高等级", "err"); return false; }
    if (State.coins < r.upgrade_costs[rs.level]) { toast("金币不足", "err"); return false; }
  } else {
    return false;
  }

  const snap = snapshotForCommand();
  let message = "", kind = "ok";

  if (act === "unlock") {
    State.coins -= r.unlock_cost;
    rs.unlocked = true;
    rs.slots = r.initial_slots;
    rs.slotBusy = new Array(rs.slots).fill(null);
    // 注意：开放科室不计入 investments（T04 的口径是"提速或扩容"）。
    // 这里也【不再】发放开业补助，一次性开业补助的唯一口径是 T01。
    message = `已开放 ${r.name}！${r.area}区新增服务`;
  } else if (act === "slot") {
    const slotIdx = rs.slots - r.initial_slots;
    State.coins -= r.slot_costs[slotIdx];
    rs.slots++;
    rs.slotBusy.push(null);
    State.stats.investments++;
    message = `${r.name} 扩容至 ${rs.slots} 工位`;
  } else {
    State.coins -= r.upgrade_costs[rs.level];
    rs.level++;
    State.stats.investments++;
    message = `${r.name} 提速至 Lv${rs.level}`;
  }

  if (!saveGame(true)) {
    restoreForCommand(snap);
    toast("保存失败，本次改动已撤销", "err");
    renderRoomPanel();
    return false;
  }
  toast(message, kind);
  renderRoomPanel();
  return true;
}

// ---------- 点击交互 ----------
canvas.addEventListener("click", (e) => {
  const [sx, sy] = screenToCanvas(e.clientX, e.clientY);
  for (const rid of ["GP", "PH", "TR", "LB"]) {
    const rc = roomRect(rid);
    if (sx >= rc.x && sx <= rc.x + rc.w && sy >= rc.y && sy <= rc.y + rc.h) {
      openRoomPanel(rid);
      return;
    }
  }
});

// ---------- 任务（一次性，重复不发放） ----------
function checkTasks() {
  for (const t of CFG.tasks) {
    if (State.stats.claimedTasks[t.id]) continue;
    if (!taskMet(t)) continue;
    State.stats.claimedTasks[t.id] = true;
    State.coins += t.reward;
    State.stats.grantLog.push({ id: t.id, amount: t.reward, tick: State.simTick });
    if (t.reward > 0) toast(`任务完成：${t.name} +${t.reward} 金币`, "ok");
    renderTaskBar();
  }
}
function taskMet(t) {
  const c = t.condition;
  if ("completed" in c) return State.stats.completed >= c.completed;
  if ("room" in c) return !!(State.rooms[c.room] && State.rooms[c.room].unlocked);
  if ("investments" in c) return State.stats.investments >= c.investments;
  if ("service" in c) {
    for (const rid in c.service) if ((State.stats.service[rid] || 0) < c.service[rid]) return false;
    return true;
  }
  return false;
}
function renderTaskBar() {
  const list = document.getElementById("task-list");
  if (!list) return;
  list.innerHTML = CFG.tasks.map(t => {
    const done = State.stats.claimedTasks[t.id];
    return `<span class="task-chip ${done ? "done" : ""}">${done ? "✓" : "○"} ${t.name} <span class="t-reward">+${t.reward}</span></span>`;
  }).join("");
}

// ---------- Toast ----------
let toastTimer = null;
function toast(msg, kind) {
  const el = document.getElementById("toast");
  if (!el) return;
  el.textContent = msg;
  el.className = kind || "";
  if (toastTimer) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 2200);
}

// ---------- 存档 ----------
function serialize() {
  const rooms = {};
  for (const rid in State.rooms) {
    const rs = State.rooms[rid];
    rooms[rid] = { unlocked: rs.unlocked, level: rs.level, slots: rs.slots, waiting: rs.waiting, slotBusy: rs.slotBusy };
  }
  return {
    v: SAVE_VERSION,
    version: CFG.version,
    coins: State.coins, simTick: State.simTick, speed: State.speed,
    rngState: State.rngState, nextPatientId: State.nextPatientId,
    nextArrivalTick: State.nextArrivalTick, lastSaveTick: State.lastSaveTick,
    stats: State.stats, rooms, patients: State.patients,
    regSlots: State.regSlots, regQueue: State.regQueue,
  };
}
function backupRaw(raw, tag) {
  try {
    const key = `${SAVE_KEY}.bak.${tag}.${Date.now()}`;
    localStorage.setItem(key, raw);
    return key;
  } catch (_) { return null; }
}
// 写盘 → 读回校验。读到的不一致（配额静默丢弃等）也算失败。
function saveGame(silent) {
  const raw = JSON.stringify(serialize());
  try {
    localStorage.setItem(SAVE_KEY, raw);
    const back = localStorage.getItem(SAVE_KEY);
    if (back !== raw) throw new Error("写回校验不一致");
    State.saveStatus = "ok"; State.saveError = "";
    if (!silent) toast("已保存", "ok");
    return true;
  } catch (e) {
    State.saveStatus = "failed";
    State.saveError = String(e && e.message || e);
    if (!silent) toast("保存失败：改动已撤销", "err");
    return false;
  }
}
function isValidSnapshot(d) {
  if (!d || typeof d !== "object") return false;
  const nums = ["coins", "simTick", "speed", "rngState", "nextPatientId", "nextArrivalTick", "lastSaveTick"];
  for (const k of nums) if (typeof d[k] !== "number" || !Number.isFinite(d[k])) return false;
  if (!d.stats || typeof d.stats !== "object") return false;
  if (!Array.isArray(d.patients)) return false;
  for (const p of d.patients) {
    if (!p || typeof p !== "object") return false;
    if (typeof p.x !== "number" || !Number.isFinite(p.x)) return false;
    if (typeof p.y !== "number" || !Number.isFinite(p.y)) return false;
    if (typeof p.tx !== "number" || !Number.isFinite(p.tx)) return false;
    if (typeof p.ty !== "number" || !Number.isFinite(p.ty)) return false;
    if (!Array.isArray(p.route) || p.route.length === 0) return false;
  }
  return true;
}
// 返回 {ok, reason}：未知版本、坏档、含非有限值的旧档一律拒绝载入，并保留原文备份
function loadGame() {
  let raw = null;
  try { raw = localStorage.getItem(SAVE_KEY); } catch (_) { return { ok: false, reason: "read-error" }; }
  if (raw === null || raw === undefined) return { ok: false, reason: "empty" };

  let d = null;
  try { d = JSON.parse(raw); }
  catch (e) {
    const key = backupRaw(raw, "corrupt");
    console.warn("loadGame: 存档不是合法 JSON，已备份到 " + key);
    return { ok: false, reason: "corrupt", backup: key };
  }
  if (!d || typeof d !== "object" || d.v !== SAVE_VERSION) {
    const tag = (d && d.v !== undefined) ? ("v" + d.v) : "noversion";
    const key = backupRaw(raw, tag);
    console.warn(`loadGame: 存档版本 ${d && d.v} 与本次候选 ${SAVE_VERSION} 不符，已拒绝并备份到 ${key}`);
    return { ok: false, reason: "version", backup: key, found: d && d.v };
  }
  if (!isValidSnapshot(d)) {
    const key = backupRaw(raw, "invalid");
    console.warn("loadGame: 存档结构或数值非法，已拒绝并备份到 " + key);
    return { ok: false, reason: "invalid", backup: key };
  }

  State.coins = d.coins; State.simTick = d.simTick; State.speed = d.speed;
  State.rngState = d.rngState; State.nextPatientId = d.nextPatientId;
  State.nextArrivalTick = d.nextArrivalTick; State.lastSaveTick = d.lastSaveTick;
  State.stats = d.stats;
  if (!State.stats.service) State.stats.service = {};
  if (!State.stats.claimedTasks) State.stats.claimedTasks = {};
  if (!State.stats.grantLog) State.stats.grantLog = [];
  if (!State.stats.transferReasons) State.stats.transferReasons = { patience: 0, admission: 0, brokenRoute: 0 };
  for (const rid in d.rooms) if (State.rooms[rid]) Object.assign(State.rooms[rid], d.rooms[rid]);
  State.patients = d.patients;
  State.regSlots = Array.isArray(d.regSlots) && d.regSlots.length === IN.registration_slots
    ? d.regSlots : new Array(IN.registration_slots).fill(null);
  State.regQueue = Array.isArray(d.regQueue) ? d.regQueue : [];
  // 恢复后自洽性检查：被引用但已不在场的患者一律清理
  const alive = new Set(State.patients.map(p => p.id));
  State.regQueue = State.regQueue.filter(id => alive.has(id));
  for (let i = 0; i < State.regSlots.length; i++) {
    if (State.regSlots[i] !== null && !alive.has(State.regSlots[i])) State.regSlots[i] = null;
  }
  for (const rid in State.rooms) {
    const rs = State.rooms[rid];
    rs.waiting = (rs.waiting || []).filter(id => alive.has(id));
    for (let s = 0; s < rs.slotBusy.length; s++) {
      const b = rs.slotBusy[s];
      if (b && !alive.has(b.patientId)) rs.slotBusy[s] = null;
    }
  }
  State.saveStatus = "ok";
  return { ok: true, reason: "loaded" };
}
function resetGame() {
  if (!confirm("确认重置？当前进度将清空。")) return;
  localStorage.removeItem(SAVE_KEY);
  State.coins = IN.initial_coins; State.simTick = 0; State.speed = 1;
  State.pausedByUser = false; State.pausedByPanel = false;
  State.patients = []; State.nextPatientId = 1;
  State.nextArrivalTick = 0; State.lastSaveTick = 0;
  State.stats = {
    completed: 0, transferred: 0, investments: 0, service: {}, claimedTasks: {},
    grantLog: [], transferReasons: { patience: 0, admission: 0, brokenRoute: 0 },
  };
  State.rngState = 12345;
  State.saveStatus = "ok"; State.saveError = "";
  initRooms();
  initQueues();
  closeRoomPanel();
  toast("已重置", "ok");
  renderTaskBar();
}

// ---------- 按钮 ----------
document.getElementById("btn-pause").addEventListener("click", () => {
  State.pausedByUser = !State.pausedByUser;
  updateHUD();
});
document.getElementById("btn-speed").addEventListener("click", () => {
  State.speed = State.speed === 1 ? 2 : State.speed === 2 ? 4 : 1;
  updateHUD();
});
document.getElementById("btn-save").addEventListener("click", () => saveGame(false));
document.getElementById("btn-reset").addEventListener("click", resetGame);

// ---------- 横屏检测 / 生命周期 ----------
function checkOrientation() {
  const hint = document.getElementById("rotate-hint");
  if (!hint) return;
  const isPortrait = window.innerWidth < window.innerHeight;
  hint.classList.toggle("show", isPortrait && window.innerWidth < 768);
}
window.addEventListener("orientationchange", checkOrientation);
window.addEventListener("resize", () => { fitCanvas(); checkOrientation(); });

// 页面隐藏：停止模拟并丢弃累加器，回来时从"现在"继续，不补跑
function onVisibilityChange() {
  if (document.hidden) { State.hidden = true; acc = 0; }
  else { State.hidden = false; acc = 0; lastTime = performance.now(); }
}
if (typeof document !== "undefined" && document.addEventListener) {
  document.addEventListener("visibilitychange", onVisibilityChange);
}
window.addEventListener("pagehide", () => { State.hidden = true; acc = 0; });
window.addEventListener("pageshow", () => { State.hidden = false; acc = 0; lastTime = performance.now(); });

// ---------- 启动 ----------
let started = false;
function start() {
  const problems = validateConfig();
  if (problems.length > 0) {
    State.bootError = problems;
    const root = document.getElementById("game-root");
    if (root) {
      root.innerHTML = `<div id="boot-error"><h1>配置不完整，已停止启动</h1>
        <p>缺失字段不会再被静默当作 0 或 NaN，必须先修好配置：</p>
        <ul>${problems.map(p => `<li>${p}</li>`).join("")}</ul></div>`;
    }
    console.error("配置校验失败，拒绝启动：", problems);
    return false;
  }
  initRooms();
  initQueues();
  const res = loadGame();
  if (res.ok) toast("已恢复上次进度", "ok");
  else if (res.reason === "version") toast(`存档版本不符（v${res.found}），已备份并新开`, "err");
  else if (res.reason === "corrupt" || res.reason === "invalid") toast("存档已损坏，已备份并新开", "err");
  else toast("欢迎来到晴天小医院", "ok");
  fitCanvas();
  checkOrientation();
  renderTaskBar();
  updateHUD();
  started = true;
  requestAnimationFrame((t) => { lastTime = t; loop(t); });
  return true;
}
start();

// ---------- 自动化接缝（只读引用，不参与玩法） ----------
window.HOSP_DEBUG = {
  version: CFG.version,
  candidate: CFG.candidate,
  saveVersion: SAVE_VERSION,
  get state() { return State; },
  get view() { return view; },
  get paused() { return isPaused(); },
  get started() { return started; },
  api: {
    tick, serviceTicks, spawnPatient, doAction, saveGame, loadGame, resetGame,
    openRoomPanel, closeRoomPanel, renderRoomPanel, updateHUD, checkTasks, taskMet,
    slotAnchor, waitAnchor, regSlotAnchor, entryAnchor, roomRect, fitCanvas, serialize,
    designPx, patientR, MIN_CSS, ARRIVAL_INTERVAL, MAX_FRAME_DT,
  },
};
