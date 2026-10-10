// ============================================================
// 晴天小医院 v2.1 轻量原型 — 游戏主体
// 架构：模拟（10Hz tick）与渲染（rAF）分离；配置全部来自 window.HOSP_CONFIG
// ============================================================
"use strict";

const CFG = window.HOSP_CONFIG;
const IN = CFG.inputs;
const TICK_HZ = IN.tick_hz;                 // 10 tick / sim秒
const PATIENCE_TICKS = IN.patience_seconds * TICK_HZ;
const GRACE_TICKS = IN.tutorial_grace_seconds * TICK_HZ;
const REG_TICKS = IN.registration_seconds * TICK_HZ;
const SETTLE_TICKS = IN.settle_seconds * TICK_HZ;
const WALK_PER_TICK = IN.walk_units_per_second / TICK_HZ; // 16
const AUTOSAVE_EVERY = IN.autosave_sim_seconds * TICK_HZ;
const ARRIVAL_INTERVAL = Math.round(600 / CFG.arrival_per_minute); // 150 tick ≈ 15s

// 服务时长（tick）= base_ticks / (1 + level * speed_gain)
function serviceTicks(room, level) {
  return Math.max(1, Math.round(room.base_ticks / (1 + level * IN.speed_gain_per_level)));
}

// ---------- 全局状态 ----------
const State = {
  coins: IN.initial_coins,
  simTick: 0,
  paused: false,
  speed: 1,                 // 1 / 2 / 4
  rooms: {},                // roomId -> {unlocked, level, slots}
  patients: [],             // 活跃患者
  nextPatientId: 1,
  stats: { completed: 0, transferred: 0, investments: 0, service: {}, claimedTasks: {} },
  lastArrivalTick: 0,
  selectedRoom: null,
  lastSaveTick: 0,
  rngState: 12345
};

// 初始化房间状态
function initRooms() {
  State.rooms = {};
  for (const id in CFG.rooms) {
    const r = CFG.rooms[id];
    State.rooms[id] = {
      unlocked: !!r.initial,
      level: 0,
      slots: r.initial ? r.initial_slots : 0,
      waiting: [],          // patientId 队列
      slotBusy: []          // {patientId, endTick} | null
    };
  }
}
initRooms();

// 简单确定性 RNG（可存档恢复）
function rng() {
  State.rngState = (State.rngState * 1103515245 + 12345) & 0x7fffffff;
  return State.rngState / 0x7fffffff;
}
function jitter() { return IN.arrival_jitter_low + rng() * (IN.arrival_jitter_high - IN.arrival_jitter_low); }

// ---------- 病例生成 ----------
function pickCase() {
  // 按 case_weights 选 group，再在 group 内随机选一个 case
  const w = CFG.case_weights;
  const total = w.base + w.treatment + w.lab;
  let r = rng() * total;
  let group = "base";
  if ((r -= w.base) < 0) group = "base";
  else if ((r -= w.treatment) < 0) group = "treatment";
  else group = "lab";
  const candidates = CFG.cases.filter(c => c.group === group);
  return candidates[Math.floor(rng() * candidates.length)];
}

// 路线可用性检查：路线中所有房间必须已解锁
function routeAvailable(route) {
  return route.every(rid => State.rooms[rid] && State.rooms[rid].unlocked);
}

// ---------- 患者 ----------
function spawnPatient() {
  const activeCount = State.patients.length;
  if (activeCount >= IN.max_patients) return;
  // 选一个当前路线可用的病例；最多试 5 次
  let cs = null;
  for (let i = 0; i < 5; i++) {
    const c = pickCase();
    if (routeAvailable(c.route)) { cs = c; break; }
  }
  if (!cs) {
    // 退化：至少 GP+PH 一定可用（初始开放），找 base 组
    cs = CFG.cases.find(c => c.group === "base");
  }
  const entry = CFG.layout.nodes.ENTRY;
  const p = {
    id: State.nextPatientId++,
    caseId: cs.id,
    route: cs.route.slice(),
    routeIdx: -1,            // -1 表示还没到第一个科室，正在去登记
    reward: Math.round(cs.reward / IN.reward_round) * IN.reward_round,
    state: "WALK",
    x: entry[0], y: entry[1],
    tx: CFG.layout.nodes.RG[0], ty: CFG.layout.nodes.RG[1],
    patienceLeft: PATIENCE_TICKS,
    serveRoom: null, serveSlot: -1, serveEndTick: 0,
    regSlot: -1, regEndTick: 0,
    settleEndTick: 0
  };
  State.patients.push(p);
}

function roomCenter(rid) { const n = CFG.layout.nodes[rid]; return [n[0], n[1]]; }
function dist(ax, ay, bx, by) { return Math.hypot(ax - bx, ay - by); }

// ---------- 模拟 tick ----------
function tick() {
  State.simTick++;

  // 1) 服务完成 / 登记 / 结算 结束处理
  for (const p of State.patients) {
    if (p.state === "REGISTER" && State.simTick >= p.regEndTick) {
      // 登记完成，释放登记位，前往第一个科室
      p.regSlot = -1;
      p.routeIdx = 0;
      const [tx, ty] = roomCenter(p.route[0]);
      p.tx = tx; p.ty = ty; p.state = "WALK";
    } else if (p.state === "SERVE" && State.simTick >= p.serveEndTick) {
      // 服务完成，释放服务位
      const rs = State.rooms[p.serveRoom];
      rs.slotBusy[p.serveSlot] = null;
      // 服务计数
      State.stats.service[p.serveRoom] = (State.stats.service[p.serveRoom] || 0) + 1;
      p.serveRoom = null; p.serveSlot = -1;
      p.routeIdx++;
      if (p.routeIdx < p.route.length) {
        const [tx, ty] = roomCenter(p.route[p.routeIdx]);
        p.tx = tx; p.ty = ty; p.state = "WALK";
      } else {
        // 全部科室完成，前往出口
        p.tx = CFG.layout.nodes.EXIT[0]; p.ty = CFG.layout.nodes.EXIT[1];
        p.state = "LEAVE";
      }
    } else if (p.state === "SETTLE" && State.simTick >= p.settleEndTick) {
      // 结算：发奖金、计数、移除
      State.coins += p.reward;
      State.stats.completed++;
      p.state = "DONE";
    }
  }

  // 2) 行走 / 到达 / 离场
  for (const p of State.patients) {
    if (p.state === "WALK" || p.state === "LEAVE") {
      const d = dist(p.x, p.y, p.tx, p.ty);
      if (d <= WALK_PER_TICK) {
        p.x = p.tx; p.y = p.ty;
        onArrive(p);
      } else {
        p.x += (p.tx - p.x) / d * WALK_PER_TICK;
        p.y += (p.ty - p.y) / d * WALK_PER_TICK;
      }
    }
  }

  // 3) 新患者到院
  if (State.simTick - State.lastArrivalTick >= ARRIVAL_INTERVAL) {
    State.lastArrivalTick = State.simTick;
    // 抖动：再推迟/提前一点下次（通过修改 lastArrivalTick）
    State.lastArrivalTick -= Math.round(ARRIVAL_INTERVAL * (jitter() - 1));
    spawnPatient();
  }

  // 4) 队列调度：每个房间把队首送进空闲服务位
  for (const rid in State.rooms) {
    const rs = State.rooms[rid];
    if (!rs.unlocked) continue;
    // 确保 slotBusy 数组长度 = slots
    while (rs.slotBusy.length < rs.slots) rs.slotBusy.push(null);
    for (let s = 0; s < rs.slots; s++) {
      if (rs.slotBusy[s] === null && rs.waiting.length > 0) {
        const pid = rs.waiting.shift();
        const p = State.patients.find(x => x.id === pid);
        if (!p || p.state !== "WAIT") continue; // 已离开则跳过
        p.serveRoom = rid; p.serveSlot = s;
        p.serveEndTick = State.simTick + serviceTicks(CFG.rooms[rid], rs.level);
        rs.slotBusy[s] = { patientId: pid, endTick: p.serveEndTick };
        p.state = "SERVE";
      }
    }
  }

  // 5) 耐心超时（仅在 WAIT 状态累计等待；教学保护期内不转诊）
  const inGrace = State.simTick < GRACE_TICKS;
  for (let i = State.patients.length - 1; i >= 0; i--) {
    const p = State.patients[i];
    if (p.state === "WAIT") {
      p.patienceLeft--;
      if (!inGrace && p.patienceLeft <= 0) {
        // 安全转诊：移出队列、不计奖励、不重复结算
        const rs = State.rooms[p.route[p.routeIdx]];
        const qi = rs.waiting.indexOf(p.id);
        if (qi >= 0) rs.waiting.splice(qi, 1);
        State.stats.transferred++;
        p.state = "GONE";
        State.patients.splice(i, 1);
      }
    }
  }

  // 清理 DONE 患者
  for (let i = State.patients.length - 1; i >= 0; i--) {
    if (State.patients[i].state === "DONE" || State.patients[i].state === "GONE") {
      State.patients.splice(i, 1);
    }
  }

  // 自动存档
  if (State.simTick - State.lastSaveTick >= AUTOSAVE_EVERY) {
    State.lastSaveTick = State.simTick;
    saveGame(true);
  }

  // 任务检查
  checkTasks();
}

function onArrive(p) {
  if (p.state === "WALK") {
    if (p.routeIdx === -1) {
      // 到达登记台 → 开始登记
      p.state = "REGISTER";
      p.regEndTick = State.simTick + REG_TICKS;
      return;
    }
    const target = p.route[p.routeIdx];
    const rs = State.rooms[target];
    if (!rs || !rs.unlocked) {
      // 房间被锁（不该发生，路线已校验），转诊
      State.stats.transferred++; p.state = "GONE"; return;
    }
    rs.waiting.push(p.id);
    p.state = "WAIT";
  } else if (p.state === "LEAVE") {
    p.state = "SETTLE";
    p.settleEndTick = State.simTick + SETTLE_TICKS;
  }
}

// ---------- 渲染 ----------
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const LAYOUT = CFG.layout;
const ROOM_W = LAYOUT.room_size.w;
const ROOM_H = LAYOUT.room_size.h;

function fitCanvas() {
  // canvas 内部分辨率固定 1280×720；CSS 由 flex 拉伸高度，宽度按比例
  // 这里只设置内部分辨率（已固定），缩放靠 CSS
  const stage = document.getElementById("stage");
  const h = stage.clientHeight;
  const w = Math.round(h * (LAYOUT.area_width / LAYOUT.area_height));
  canvas.style.width = w + "px";
  canvas.style.height = h + "px";
  // 保留 1280×720 内部分辨率
}
window.addEventListener("resize", fitCanvas);

// 屏幕坐标 → canvas 内部坐标
function screenToCanvas(clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  const sx = (clientX - rect.left) / rect.width * canvas.width;
  const sy = (clientY - rect.top) / rect.height * canvas.height;
  return [sx, sy];
}

function roomRect(rid) {
  const [cx, cy] = LAYOUT.nodes[rid];
  return { x: cx - ROOM_W / 2, y: cy - ROOM_H / 2, w: ROOM_W, h: ROOM_H };
}

function drawRoom(rid) {
  const r = CFG.rooms[rid];
  const rs = State.rooms[rid];
  const rect = roomRect(rid);
  const selected = State.selectedRoom === rid;

  // 房间底
  if (!rs.unlocked) {
    ctx.fillStyle = "#9aa8b4";
    ctx.strokeStyle = "#6b7884";
  } else {
    ctx.fillStyle = selected ? "#fff4d6" : "#eaf3f4";
    ctx.strokeStyle = selected ? "#d97706" : "#5a8a9a";
  }
  ctx.lineWidth = selected ? 4 : 2;
  roundRect(rect.x, rect.y, rect.w, rect.h, 14);
  ctx.fill(); ctx.stroke();

  // 标题
  ctx.fillStyle = rs.unlocked ? "#135f68" : "#5a6b7c";
  ctx.font = "bold 22px system-ui";
  ctx.textAlign = "center";
  ctx.fillText(r.name, rect.x + rect.w / 2, rect.y + 32);

  // 状态文字
  ctx.font = "15px system-ui";
  if (!rs.unlocked) {
    ctx.fillStyle = "#5a6b7c";
    ctx.fillText("未开放", rect.x + rect.w / 2, rect.y + 58);
    if (r.unlock_cost > 0) {
      ctx.fillStyle = "#d97706";
      ctx.font = "bold 16px system-ui";
      ctx.fillText("投入 " + r.unlock_cost + " 金币开放", rect.x + rect.w / 2, rect.y + 82);
    }
    // 斜纹
    ctx.strokeStyle = "rgba(255,255,255,.35)";
    ctx.lineWidth = 1;
    for (let i = -rect.h; i < rect.w; i += 12) {
      ctx.beginPath();
      ctx.moveTo(rect.x + i, rect.y + rect.h);
      ctx.lineTo(rect.x + i + rect.h, rect.y);
      ctx.stroke();
    }
  } else {
    const busy = rs.slotBusy.filter(s => s).length;
    ctx.fillStyle = "#1a8a9e";
    ctx.fillText("工位 " + busy + "/" + rs.slots, rect.x + rect.w / 2, rect.y + 58);
    ctx.fillStyle = rs.waiting.length > 0 ? "#d97706" : "#5a8a9e";
    ctx.fillText("等候 " + rs.waiting.length + " 人", rect.x + rect.w / 2, rect.y + 80);
    if (rs.level > 0) {
      ctx.fillStyle = "#7a8a99";
      ctx.font = "12px system-ui";
      ctx.fillText("提速 Lv" + rs.level, rect.x + rect.w / 2, rect.y + 100);
    }
  }
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

function drawCorridor() {
  // 走廊横线
  ctx.strokeStyle = "#b8c8d2";
  ctx.lineWidth = 6;
  ctx.beginPath();
  ctx.moveTo(LAYOUT.nodes.ENTRY[0], LAYOUT.corridor_y);
  ctx.lineTo(LAYOUT.nodes.EXIT[0], LAYOUT.corridor_y);
  ctx.stroke();

  // 入口/出口标记
  ctx.fillStyle = "#2a9e6a";
  ctx.font = "bold 16px system-ui";
  ctx.textAlign = "center";
  ctx.fillText("入口", LAYOUT.nodes.ENTRY[0], LAYOUT.corridor_y - 18);
  ctx.fillStyle = "#9e4a2a";
  ctx.fillText("出口", LAYOUT.nodes.EXIT[0], LAYOUT.corridor_y - 18);

  // 登记台
  const rg = LAYOUT.nodes.RG;
  ctx.fillStyle = "#f4d6a0";
  ctx.strokeStyle = "#c89a4a";
  roundRect(rg[0] - 35, rg[1] - 22, 70, 44, 8);
  ctx.fill(); ctx.stroke();
  ctx.fillStyle = "#7a5a1a";
  ctx.font = "13px system-ui";
  ctx.fillText("登记", rg[0], rg[1] + 5);

  // B/C 区占位提示
  ctx.fillStyle = "rgba(120,134,148,.5)";
  ctx.font = "bold 16px system-ui";
  ctx.textAlign = "left";
  ctx.fillText("B区 / C区 · 后续内容（本期不可开放）", 1040, 690);
}

function drawPatient(p) {
  let color = "#4a8adf";
  if (p.state === "WAIT") color = "#d97706";
  else if (p.state === "SERVE") color = "#2a9e6a";
  else if (p.state === "REGISTER") color = "#9a6adf";
  else if (p.state === "LEAVE" || p.state === "SETTLE") color = "#2a9e6a";

  ctx.fillStyle = color;
  ctx.strokeStyle = "#fff";
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.arc(p.x, p.y, 8, 0, Math.PI * 2);
  ctx.fill(); ctx.stroke();

  // 等待时显示耐心条
  if (p.state === "WAIT") {
    const ratio = Math.max(0, p.patienceLeft / PATIENCE_TICKS);
    ctx.fillStyle = "rgba(0,0,0,.3)";
    ctx.fillRect(p.x - 10, p.y - 16, 20, 3);
    ctx.fillStyle = ratio > 0.4 ? "#2a9e6a" : ratio > 0.2 ? "#d97706" : "#d04040";
    ctx.fillRect(p.x - 10, p.y - 16, 20 * ratio, 3);
  }
}

function render() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  // 背景
  ctx.fillStyle = "#c8d8e2";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  // 地板纹理（简单网格）
  ctx.strokeStyle = "rgba(255,255,255,.25)";
  ctx.lineWidth = 1;
  for (let x = 0; x < canvas.width; x += 64) {
    ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, canvas.height); ctx.stroke();
  }
  for (let y = 0; y < canvas.height; y += 64) {
    ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(canvas.width, y); ctx.stroke();
  }

  drawCorridor();

  // A 区 4 房间
  ["GP", "PH", "TR", "LB"].forEach(drawRoom);

  // 患者
  for (const p of State.patients) drawPatient(p);
}

// ---------- 主循环 ----------
let acc = 0;
let lastTime = performance.now();
function loop(now) {
  const dt = (now - lastTime) / 1000;
  lastTime = now;
  if (!State.paused) {
    acc += dt * State.speed;
    const tickSec = 1 / TICK_HZ;
    let guard = 0;
    while (acc >= tickSec && guard < 200) { tick(); acc -= tickSec; guard++; }
  }
  render();
  updateHUD();
  requestAnimationFrame(loop);
}

// ---------- HUD ----------
function updateHUD() {
  document.querySelector("#hud-coins b").textContent = State.coins;
  document.querySelector("#hud-patients b").textContent = State.patients.length;
  document.querySelector("#hud-completed b").textContent = State.stats.completed;
}

// ---------- 房间面板 ----------
const panel = document.getElementById("room-panel");
const rpBody = document.getElementById("rp-body");
const rpTitle = document.getElementById("rp-title");

function openRoomPanel(rid) {
  State.selectedRoom = rid;
  panel.classList.remove("hidden");
  renderRoomPanel();
}
function closeRoomPanel() {
  State.selectedRoom = null;
  panel.classList.add("hidden");
}
document.getElementById("rp-close").addEventListener("click", closeRoomPanel);

function renderRoomPanel() {
  const rid = State.selectedRoom;
  if (!rid) return;
  const r = CFG.rooms[rid];
  const rs = State.rooms[rid];
  rpTitle.textContent = r.name;

  if (r.locked) {
    rpBody.innerHTML = `
      <div class="rp-desc">${r.desc}</div>
      <div class="rp-locked-msg">该科室属于${r.area}区后续内容，本期原型暂不开放。</div>
    `;
    return;
  }

  const busy = rs.slotBusy.filter(s => s).length;
  const slotsHtml = `
    <div class="rp-stat"><span>状态</span><b>${rs.unlocked ? "已开放" : "未开放"}</b></div>
    <div class="rp-stat"><span>当前服务</span><b>${busy} / ${rs.slots} 工位</b></div>
    <div class="rp-stat"><span>等候人数</span><b>${rs.waiting.length} 人</b></div>
    <div class="rp-stat"><span>提速等级</span><b>Lv${rs.level}</b></div>
    <div class="rp-stat"><span>当前服务时长</span><b>${(serviceTicks(r, rs.level)/TICK_HZ).toFixed(1)} 秒</b></div>
  `;

  let actionsHtml = "";
  if (!rs.unlocked) {
    const can = State.coins >= r.unlock_cost;
    actionsHtml = `
      <div class="rp-desc">${r.desc}</div>
      <div class="rp-section-title">开放科室</div>
      <button class="rp-btn unlock" ${can?"":"disabled"} data-act="unlock" data-rid="${rid}">
        <span class="rp-btn-title">投入金币开放</span>
        <span class="rp-btn-sub">费用 ${r.unlock_cost} 金币 · 含基础设备与人员 · 立即启用</span>
      </button>
    `;
  } else {
    // 扩容
    const slotIdx = rs.slots - r.initial_slots; // 已扩几次
    const nextSlotCost = r.slot_costs[slotIdx];
    const canSlot = slotIdx < r.slot_costs.length && State.coins >= nextSlotCost;
    let slotBtn = "";
    if (slotIdx < r.slot_costs.length) {
      slotBtn = `
        <button class="rp-btn" ${canSlot?"":"disabled"} data-act="slot" data-rid="${rid}">
          <span class="rp-btn-title">扩容：增加服务位</span>
          <span class="rp-btn-sub">${rs.slots} → ${rs.slots+1} 工位 · 费用 ${nextSlotCost} 金币</span>
        </button>`;
    } else {
      slotBtn = `<div class="rp-locked-msg">已达最大 ${r.max_slots} 工位</div>`;
    }
    // 提速
    const lvlIdx = rs.level; // 0..len-1
    const nextLvlCost = r.upgrade_costs[lvlIdx];
    const canLvl = lvlIdx < r.upgrade_costs.length && State.coins >= nextLvlCost;
    let lvlBtn = "";
    if (lvlIdx < r.upgrade_costs.length) {
      const newSecs = (serviceTicks(r, rs.level+1)/TICK_HZ).toFixed(1);
      const curSecs = (serviceTicks(r, rs.level)/TICK_HZ).toFixed(1);
      lvlBtn = `
        <button class="rp-btn" ${canLvl?"":"disabled"} data-act="speed" data-rid="${rid}">
          <span class="rp-btn-title">提速：缩短服务时间</span>
          <span class="rp-btn-sub">Lv${rs.level} → Lv${rs.level+1} · ${curSecs}秒 → ${newSecs}秒 · 费用 ${nextLvlCost} 金币</span>
        </button>`;
    } else {
      lvlBtn = `<div class="rp-locked-msg">已达最高提速等级</div>`;
    }
    actionsHtml = `
      <div class="rp-desc">${r.desc}</div>
      ${slotsHtml}
      <div class="rp-section-title">投资</div>
      ${slotBtn}
      ${lvlBtn}
    `;
  }
  rpBody.innerHTML = actionsHtml;

  // 绑定按钮
  rpBody.querySelectorAll("button[data-act]").forEach(btn => {
    btn.addEventListener("click", () => doAction(btn.dataset.act, btn.dataset.rid));
  });
}

// ---------- 玩家命令 ----------
function doAction(act, rid) {
  const r = CFG.rooms[rid];
  const rs = State.rooms[rid];
  if (act === "unlock") {
    if (rs.unlocked) return toast("已开放", "err");
    if (State.coins < r.unlock_cost) return toast("金币不足", "err");
    State.coins -= r.unlock_cost;
    rs.unlocked = true;
    rs.slots = r.initial_slots;
    rs.slotBusy = new Array(rs.slots).fill(null);
    toast(`已开放 ${r.name}！${r.area}区新增服务`, "ok");
    // 开业补助：首次开放 TR 或 LB 时给一笔启动金（对应 opening_grant）
    if (rid === "TR" || rid === "LB") {
      State.coins += IN.opening_grant;
      toast(`开业补助 +${IN.opening_grant} 金币`, "ok");
    }
  } else if (act === "slot") {
    const slotIdx = rs.slots - r.initial_slots;
    if (slotIdx >= r.slot_costs.length) return toast("已达最大工位", "err");
    const cost = r.slot_costs[slotIdx];
    if (State.coins < cost) return toast("金币不足", "err");
    State.coins -= cost;
    rs.slots++;
    rs.slotBusy.push(null);
    State.stats.investments++;
    toast(`${r.name} 扩容至 ${rs.slots} 工位`, "ok");
  } else if (act === "speed") {
    if (rs.level >= r.upgrade_costs.length) return toast("已达最高等级", "err");
    const cost = r.upgrade_costs[rs.level];
    if (State.coins < cost) return toast("金币不足", "err");
    State.coins -= cost;
    rs.level++;
    State.stats.investments++;
    toast(`${r.name} 提速至 Lv${rs.level}`, "ok");
  }
  saveGame(true);
  renderRoomPanel();
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

// ---------- 任务 ----------
function checkTasks() {
  for (const t of CFG.tasks) {
    if (State.stats.claimedTasks[t.id]) continue;
    if (taskMet(t)) {
      State.stats.claimedTasks[t.id] = true;
      State.coins += t.reward;
      if (t.reward > 0) toast(`任务完成：${t.name} +${t.reward} 金币`, "ok");
      renderTaskBar();
    }
  }
}
function taskMet(t) {
  const c = t.condition;
  if ("completed" in c) return State.stats.completed >= c.completed;
  if ("room" in c) return State.rooms[c.room] && State.rooms[c.room].unlocked;
  if ("investments" in c) return State.stats.investments >= c.investments;
  if ("service" in c) {
    for (const rid in c.service) {
      if ((State.stats.service[rid] || 0) < c.service[rid]) return false;
    }
    return true;
  }
  return false;
}
function renderTaskBar() {
  const list = document.getElementById("task-list");
  list.innerHTML = CFG.tasks.map(t => {
    const done = State.stats.claimedTasks[t.id];
    return `<span class="task-chip ${done?"done":""}">${done?"✓":"○"} ${t.name} <span class="t-reward">+${t.reward}</span></span>`;
  }).join("");
}

// ---------- Toast ----------
let toastTimer = null;
function toast(msg, kind) {
  const el = document.getElementById("toast");
  el.textContent = msg;
  el.className = kind || "";
  if (toastTimer) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 1800);
}

// ---------- 存档 ----------
const SAVE_KEY = "sunny_hospital_save_v1";
function saveGame(silent) {
  try {
    const data = {
      v: 1,
      coins: State.coins, simTick: State.simTick, speed: State.speed,
      rngState: State.rngState, nextPatientId: State.nextPatientId,
      lastArrivalTick: State.lastArrivalTick, lastSaveTick: State.lastSaveTick,
      stats: State.stats,
      rooms: {}, patients: State.patients
    };
    for (const rid in State.rooms) {
      const rs = State.rooms[rid];
      data.rooms[rid] = { unlocked: rs.unlocked, level: rs.level, slots: rs.slots, waiting: rs.waiting, slotBusy: rs.slotBusy };
    }
    localStorage.setItem(SAVE_KEY, JSON.stringify(data));
    if (!silent) toast("已保存", "ok");
  } catch (e) { if (!silent) toast("保存失败", "err"); }
}
function loadGame() {
  try {
    const raw = localStorage.getItem(SAVE_KEY);
    if (!raw) return false;
    const d = JSON.parse(raw);
    State.coins = d.coins; State.simTick = d.simTick; State.speed = d.speed;
    State.rngState = d.rngState; State.nextPatientId = d.nextPatientId;
    State.lastArrivalTick = d.lastArrivalTick; State.lastSaveTick = d.lastSaveTick;
    State.stats = d.stats;
    for (const rid in d.rooms) {
      Object.assign(State.rooms[rid], d.rooms[rid]);
    }
    State.patients = d.patients || [];
    return true;
  } catch (e) { console.warn("load failed", e); return false; }
}
function resetGame() {
  if (!confirm("确认重置？当前进度将清空。")) return;
  localStorage.removeItem(SAVE_KEY);
  State.coins = IN.initial_coins; State.simTick = 0; State.speed = 1; State.paused = false;
  State.patients = []; State.nextPatientId = 1; State.lastArrivalTick = 0; State.lastSaveTick = 0;
  State.stats = { completed: 0, transferred: 0, investments: 0, service: {}, claimedTasks: {} };
  State.rngState = 12345;
  initRooms();
  closeRoomPanel();
  toast("已重置", "ok");
  renderTaskBar();
}

// ---------- 按钮 ----------
document.getElementById("btn-pause").addEventListener("click", () => {
  State.paused = !State.paused;
  document.getElementById("btn-pause").textContent = State.paused ? "▶ 继续" : "⏸ 暂停";
});
document.getElementById("btn-speed").addEventListener("click", () => {
  State.speed = State.speed === 1 ? 2 : State.speed === 2 ? 4 : 1;
  document.getElementById("btn-speed").textContent = State.speed + "×";
});
document.getElementById("btn-save").addEventListener("click", () => saveGame(false));
document.getElementById("btn-reset").addEventListener("click", resetGame);

// ---------- 横屏检测 ----------
function checkOrientation() {
  const hint = document.getElementById("rotate-hint");
  const isPortrait = window.innerWidth < window.innerHeight;
  hint.classList.toggle("show", isPortrait && window.innerWidth < 768);
}
window.addEventListener("orientationchange", checkOrientation);
window.addEventListener("resize", checkOrientation);

// ---------- 启动 ----------
function start() {
  if (loadGame()) {
    toast("已恢复上次进度", "ok");
  } else {
    toast("欢迎来到晴天小医院", "ok");
  }
  fitCanvas();
  checkOrientation();
  renderTaskBar();
  requestAnimationFrame((t) => { lastTime = t; loop(t); });
}
start();
