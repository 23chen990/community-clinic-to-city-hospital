// ============================================================================
// harness.js — 与真实页面同源的确定性测试驱动
//
// 加载方式与 index.html 完全一致：
//   1. 读入真实 index.html，用 jsdom 建立真实 DOM
//   2. 在 jsdom 自己的 JS 上下文里，按 <script> 顺序依次执行
//      config.js → game.js
//   3. 只补浏览器表面（canvas 2D 桩、可控 rAF、可控时钟、可控存储）
//
// 关键约束：本驱动【绝不】向游戏配置注入任何默认值。
// 配置缺字段就是缺字段，模拟必须表现出真实页面会表现的行为。
// ============================================================================
"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const crypto = require("crypto");

const NODE_MODULES = "/Users/kker/.workbuddy/binaries/node/workspace/node_modules";
const { JSDOM } = require(path.join(NODE_MODULES, "jsdom"));

// ---------------------------------------------------------------- 2D 上下文桩
function make2dStub() {
  const target = {
    canvas: null,
    fillStyle: "", strokeStyle: "", lineWidth: 1, font: "", textAlign: "left",
  };
  return new Proxy(target, {
    get(t, prop) {
      if (prop in t) return t[prop];
      // 记录被调用过的绘制 API，便于断言"画了工位/等候位"
      return function () { return undefined; };
    },
    set(t, prop, value) { t[prop] = value; return true; },
  });
}

// ---------------------------------------------------------------- 可控存储
function makeStorage(opts) {
  const map = new Map();
  const ctl = {
    failWrites: 0,        // 接下来 N 次 setItem 抛异常
    silentDrop: 0,        // 接下来 N 次 setItem 静默丢弃（写入成功假象，读回不一致）
    reads: 0, writes: 0,
  };
  const api = {
    getItem(k) {
      ctl.reads++;
      return map.has(String(k)) ? map.get(String(k)) : null;
    },
    setItem(k, v) {
      ctl.writes++;
      if (ctl.failWrites > 0) { ctl.failWrites--; throw new Error("QuotaExceededError (injected)"); }
      if (ctl.silentDrop > 0) { ctl.silentDrop--; return; }
      map.set(String(k), String(v));
    },
    removeItem(k) { map.delete(String(k)); },
    clear() { map.clear(); },
    key(i) { return Array.from(map.keys())[i] ?? null; },
  };
  Object.defineProperty(api, "length", { get() { return map.size; } });
  Object.defineProperty(api, "_map", { get() { return map; } });
  Object.defineProperty(api, "_ctl", { get() { return ctl; } });
  return api;
}

// ---------------------------------------------------------------- 环境
function createEnv(srcDir, options = {}) {
  const src = {
    html: fs.readFileSync(path.join(srcDir, "index.html"), "utf8"),
    css: fs.readFileSync(path.join(srcDir, "style.css"), "utf8"),
    config: fs.readFileSync(path.join(srcDir, "config.js"), "utf8"),
    game: fs.readFileSync(path.join(srcDir, "game.js"), "utf8"),
  };

  const dom = new JSDOM(src.html, {
    runScripts: "outside-only",
    pretendToBeVisual: false,
    url: "http://127.0.0.1/",
  });
  const { window } = dom;
  const doc = window.document;

  // canvas 2D 桩（jsdom 未编译 canvas 支持时会返回 null）
  window.HTMLCanvasElement.prototype.getContext = function () {
    return make2dStub();
  };

  // jsdom 不做排版，clientWidth/Height 与 getBoundingClientRect 恒为 0，
  // 会让页面里的坐标换算（screenToCanvas / fitCanvas）除零。
  // 这里只补"浏览器表面"：给一个固定的逻辑视口，不触碰任何游戏配置。
  const vp = options.viewport || { w: 1280, h: 720 };
  for (const [prop, val] of [["clientWidth", vp.w], ["clientHeight", vp.h],
    ["offsetWidth", vp.w], ["offsetHeight", vp.h]]) {
    Object.defineProperty(window.HTMLElement.prototype, prop, {
      get() { return val; }, configurable: true,
    });
  }
  window.Element.prototype.getBoundingClientRect = function () {
    const isCanvas = this && this.id === "canvas";
    const w = isCanvas ? vp.w : vp.w;
    const h = isCanvas ? vp.h : vp.h;
    return {
      x: 0, y: 0, left: 0, top: 0, right: w, bottom: h, width: w, height: h,
      toJSON() { return { width: w, height: h }; },
    };
  };

  // 可控存储
  const storage = makeStorage();
  Object.defineProperty(window, "localStorage", {
    value: storage, configurable: true, writable: true,
  });

  // 可控 rAF：不自动跑，由测试逐步驱动
  let pendingFrame = null;
  let rafId = 0;
  const rafLog = [];
  window.requestAnimationFrame = function (cb) {
    pendingFrame = cb;
    return ++rafId;
  };
  window.cancelAnimationFrame = function () { pendingFrame = null; };

  // jsdom 的 confirm 未实现，返回 true 以便测试 resetGame
  window.confirm = () => true;
  window.alert = () => undefined;

  // 可切换的可见性：jsdom 默认 document.hidden = true，会掩盖"后台不补跑"的真实行为
  const vis = { hidden: false };
  for (const [prop, get] of [
    ["hidden", () => vis.hidden],
    ["visibilityState", () => (vis.hidden ? "hidden" : "visible")],
  ]) {
    try {
      Object.defineProperty(doc, prop, { get, configurable: true });
    } catch (_) {
      Object.defineProperty(window.Document.prototype, prop, { get, configurable: true });
    }
  }

  const ctx = dom.getInternalVMContext();

  // 按页面顺序执行脚本：config.js → game.js
  const bootErrors = [];
  let cfgOk = true, gameOk = true;
  try {
    vm.runInContext(src.config, ctx, { filename: "config.js" });
  } catch (e) { cfgOk = false; bootErrors.push({ file: "config.js", error: String(e && e.message || e) }); }
  try {
    vm.runInContext(src.game, ctx, { filename: "game.js" });
  } catch (e) { gameOk = false; bootErrors.push({ file: "game.js", error: String(e && e.message || e) }); }

  const env = {
    srcDir, src, dom, window, document: doc, ctx, storage,
    bootErrors, cfgOk, gameOk,
    rafLog,

    /** 在页面上下文里求值（可访问 page 的全局词法绑定） */
    run(expr) { return vm.runInContext(expr, ctx, { filename: "probe.js" }); },

    /** 推进 N 个模拟 tick（直接调用页面里的 tick()，不经 rAF） */
    step(n) {
      const t = vm.runInContext("tick", ctx);
      for (let i = 0; i < n; i++) t();
      return n;
    },
    tickOnce() { return vm.runInContext("tick", ctx)(); },

    /** 驱动真实 rAF 循环一帧，nowMs 为传给回调的时间戳 */
    frame(nowMs) {
      const cb = pendingFrame;
      if (!cb) return false;
      pendingFrame = null;
      rafLog.push(nowMs);
      cb(nowMs);
      return true;
    },
    hasPendingFrame() { return pendingFrame !== null; },

    /** 切换页面可见性并派发真实的 visibilitychange 事件 */
    setHidden(h) {
      vis.hidden = !!h;
      doc.dispatchEvent(new window.Event("visibilitychange"));
    },
    isHidden() { return vis.hidden; },

    /** 触发真实点击（走 addEventListener 注册的 handler） */
    click(selector) {
      const el = doc.querySelector(selector);
      if (!el) throw new Error("click: 找不到元素 " + selector);
      el.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true }));
      return el;
    },
    /** 触发带坐标的 canvas 点击（用 canvas 内部坐标） */
    clickCanvasAt(internalX, internalY) {
      const canvas = doc.getElementById("canvas");
      const rect = canvas.getBoundingClientRect();
      const w = rect.width || 1280, h = rect.height || 720;
      const clientX = (internalX / canvas.width) * w;
      const clientY = (internalY / canvas.height) * h;
      const ev = new window.MouseEvent("click", {
        bubbles: true, cancelable: true, clientX, clientY,
      });
      Object.defineProperty(ev, "clientX", { value: clientX });
      Object.defineProperty(ev, "clientY", { value: clientY });
      canvas.dispatchEvent(ev);
    },
    /** 触发真实房间点击（走游戏里的坐标换算逻辑） */
    clickRoom(rid) {
      const node = env.run(`CFG.layout.nodes.${rid}`);
      env.clickCanvasAt(node[0], node[1]);
    },

    panelOpen() {
      const p = doc.getElementById("room-panel");
      return !p.classList.contains("hidden");
    },
    panelHtml() { return doc.getElementById("rp-body").innerHTML; },
    panelButton(act) {
      return doc.querySelector(`#rp-body button[data-act="${act}"]`);
    },
    toastText() { return doc.getElementById("toast").textContent; },
    toastClass() { return doc.getElementById("toast").className; },
    hudText() {
      return {
        coins: doc.querySelector("#hud-coins b")?.textContent,
        patients: doc.querySelector("#hud-patients b")?.textContent,
        completed: doc.querySelector("#hud-completed b")?.textContent,
        pause: doc.getElementById("btn-pause")?.textContent,
        speed: doc.getElementById("btn-speed")?.textContent,
      };
    },

    /** 把页面里某个表达式的结果快照成普通对象（跨 VM 边界） */
    snap(expr) { return JSON.parse(JSON.stringify(env.run(`(${expr})`))); },

    hash() {
      const h = (f) => crypto.createHash("sha256").update(src[f]).digest("hex");
      return {
        "index.html": h("html"), "style.css": h("css"),
        "config.js": h("config"), "game.js": h("game"),
      };
    },
    dispose() { try { window.close(); } catch (_) {} },
  };

  return env;
}

// ---------------------------------------------------------------- 断言
function makeReporter(title, out) {
  const log = out || ((s) => console.log(s));
  const r = {
    title, pass: 0, fail: 0, results: [],
    log,
    line(s) { log(s); },
    check(id, desc, ok, detail) {
      r.results.push({ id, desc, ok, detail });
      if (ok) { r.pass++; log(`  PASS  ${id}  ${desc}`); }
      else { r.fail++; log(`  FAIL  ${id}  ${desc}`); if (detail) log(`        ↳ ${detail}`); }
      return ok;
    },
    eq(id, desc, actual, expected) {
      const ok = JSON.stringify(actual) === JSON.stringify(expected);
      return r.check(id, desc, ok, ok ? "" : `actual=${JSON.stringify(actual)} expected=${JSON.stringify(expected)}`);
    },
    close(id, desc, actual, expected, tol) {
      const ok = typeof actual === "number" && isFinite(actual) && Math.abs(actual - expected) <= tol;
      return r.check(id, desc, ok, ok ? "" : `actual=${actual} expected=${expected} ±${tol}`);
    },
    finite(id, desc, v) {
      const ok = typeof v === "number" && Number.isFinite(v);
      return r.check(id, desc, ok, ok ? "" : `value=${v} (非有限值)`);
    },
    section(s) { log(""); log(`── ${s} ──`); },
    summary() {
      log("");
      log(`合计：${r.pass + r.fail} 项，PASS ${r.pass}，FAIL ${r.fail}`);
      return { total: r.pass + r.fail, pass: r.pass, fail: r.fail, results: r.results };
    },
  };
  return r;
}

module.exports = { createEnv, makeReporter, NODE_MODULES };
