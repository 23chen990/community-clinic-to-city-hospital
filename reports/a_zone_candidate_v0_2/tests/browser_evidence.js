// ============================================================================
// browser_evidence.js — 真实浏览器证据
//   1) 三个横屏视口截图（修复前 origin_frozen vs 修复后 prototype）
//   2) 量化的越界 / 缩放 / 可读性对比
//   3) 连续开业到检验室运转的录屏（CDP screencast → ffmpeg 合成 mp4）
//
// 用真实 Chromium（puppeteer 自带的 chrome）加载真实 index.html，
// 通过本机 http 服务访问，不使用 set_content 拼接。
// ============================================================================
"use strict";

const path = require("path");
const fs = require("fs");
const http = require("http");
const { spawnSync } = require("child_process");

const NODE_MODULES = "/Users/kker/.workbuddy/binaries/node/workspace/node_modules";
const puppeteer = require(path.join(NODE_MODULES, "puppeteer"));
const FFMPEG = require(path.join(NODE_MODULES, "ffmpeg-static"));

const ROOT = path.resolve(__dirname, "..");
const HOSP = path.resolve(ROOT, "..", "..");
const CAND = path.join(HOSP, "prototype");
const ORIGIN = path.join(ROOT, "origin_frozen");
const SHOTS = path.join(ROOT, "shots");
const FRAMES = path.join(ROOT, "frames");
const REC = path.join(ROOT, "recording");
const LOG = path.join(ROOT, "logs", "browser_evidence.log");

const lines = [];
const out = (s) => { lines.push(s); process.stdout.write(s + "\n"); };

const VIEWPORTS = [
  { name: "1280x720", width: 1280, height: 720 },
  { name: "844x390", width: 844, height: 390 },
  { name: "667x375", width: 667, height: 375 },
];

// ---------------------------------------------------------------- 静态服务
function serve(dir) {
  const types = { ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "application/javascript; charset=utf-8" };
  const server = http.createServer((req, res) => {
    const p = decodeURIComponent(req.url.split("?")[0]);
    const file = path.join(dir, p === "/" ? "index.html" : p);
    if (!file.startsWith(dir) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      res.writeHead(404); res.end("not found"); return;
    }
    res.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream" });
    fs.createReadStream(file).pipe(res);
  });
  return new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port })));
}

// ---------------------------------------------------------------- 页面测量
async function measure(page) {
  return page.evaluate(() => {
    const de = document.documentElement;
    const panel = document.getElementById("room-panel");
    const canvas = document.getElementById("canvas");
    const cr = canvas.getBoundingClientRect();
    const pr = panel ? panel.getBoundingClientRect() : null;
    const panelVisible = panel && !panel.classList.contains("hidden");
    const D = window.HOSP_DEBUG;
    const scale = D ? D.view.scale : null;
    const px = (base, minCss) => (D ? D.api.designPx(base, minCss) * D.view.scale : null);
    return {
      innerW: window.innerWidth, innerH: window.innerHeight,
      docScrollW: de.scrollWidth, docClientW: de.clientWidth,
      overflowX: de.scrollWidth - de.clientWidth,
      bodyScrollW: document.body.scrollWidth,
      panelVisible,
      panelRight: pr ? pr.right : null,
      panelLeft: pr ? pr.left : null,
      panelW: pr ? pr.width : null,
      panelOverflowRight: pr ? Math.max(0, pr.right - window.innerWidth) : null,
      panelOverflowBottom: pr ? Math.max(0, pr.bottom - window.innerHeight) : null,
      canvasLeft: cr.left, canvasTop: cr.top, canvasW: cr.width, canvasH: cr.height,
      canvasBottom: cr.bottom,
      canvasAspect: cr.height > 0 ? +(cr.width / cr.height).toFixed(4) : null,
      canvasOverflowBottom: Math.max(0, cr.bottom - window.innerHeight),
      scale,
      // 画布自身的等比系数：上传版没有 HOSP_DEBUG，用它反推修复前的实际字号/患者大小
      canvasScale: cr.width / 1280,
      titleCssPx: px(22, 14),
      titleCssPxFromCanvas: 22 * (cr.width / 1280),
      bodyCssPx: px(15, 10.5),
      patientCssPx: D ? D.api.patientR() * D.view.scale : null,
      patientCssPxFromCanvas: 2 * 8 * (cr.width / 1280),
      closeBtnRect: (() => {
        const b = document.getElementById("rp-close");
        if (!b || !panelVisible) return null;
        const r = b.getBoundingClientRect();
        return { x: r.left, y: r.top, right: r.right, bottom: r.bottom, inView: r.right <= window.innerWidth + 1 && r.bottom <= window.innerHeight + 1 && r.top >= -1 && r.left >= -1 };
      })(),
      unlockBtnRect: (() => {
        const b = document.querySelector('#rp-body button[data-act="unlock"], #rp-body button[data-act="slot"]');
        if (!b || !panelVisible) return null;
        const r = b.getBoundingClientRect();
        return { right: r.right, bottom: r.bottom, w: r.width, inView: r.right <= window.innerWidth + 1 && r.bottom <= window.innerHeight + 1 };
      })(),
      hudScrollW: document.getElementById("hud").scrollWidth,
      hudClientW: document.getElementById("hud").clientWidth,
    };
  });
}

async function shotFor(page, dir, vp, kind, target) {
  const file = path.join(dir, `landscape_${vp.name}${kind ? "_" + kind : ""}.png`);
  await page.screenshot({ path: file });
  return path.relative(HOSP, file);
}

// 与页面无关的取室矩形：候选走 HOSP_DEBUG，上传版直接读它的 CFG
async function roomRectOf(page, rid) {
  return page.evaluate((id) => {
    const D = window.HOSP_DEBUG;
    if (D) return D.api.roomRect(id);
    const c = CFG.layout, w = c.room_size.w, h = c.room_size.h, n = c.nodes[id];
    return { x: n[0] - w / 2, y: n[1] - h / 2, w, h };
  }, rid);
}
async function advanceSim(page, n) {
  return page.evaluate((count) => {
    const D = window.HOSP_DEBUG;
    if (D) { for (let i = 0; i < count; i++) D.api.tick(); return "HOSP_DEBUG"; }
    if (typeof tick === "function") { for (let i = 0; i < count; i++) tick(); return "tick"; }
    return "none";
  }, n);
}

async function captureViewport(browser, base, prefix, vp, report) {
  const page = await browser.newPage();
  await page.setViewport({ width: vp.width, height: vp.height, deviceScaleFactor: 1 });
  const errs = [];
  page.on("pageerror", (e) => errs.push(String(e.message)));
  page.on("console", (m) => { if (m.type() === "error") errs.push("console: " + m.text()); });

  // 1) 载入 + 让模拟跑一段（保证画面上有患者/工位）
  await page.goto(`${base}/index.html`, { waitUntil: "load" });
  await page.evaluate(() => { try { localStorage.clear(); } catch (e) {} });
  await page.reload({ waitUntil: "load" });
  await new Promise((r) => setTimeout(r, 300));
  await advanceSim(page, 900);
  await new Promise((r) => setTimeout(r, 2600));   // 等起始 toast 淡出，画面干净
  const base_measure = await measure(page);
  const mainShot = await shotFor(page, SHOTS, vp, prefix ? `${prefix}_main` : "main");

  const canvasBox = await page.evaluate(() => {
    const c = document.getElementById("canvas");
    const r = c.getBoundingClientRect();
    return { x: r.left, y: r.top, w: r.width, h: r.height };
  });
  const clickRoom = async (rid) => {
    const n = await roomRectOf(page, rid);
    await page.mouse.click(canvasBox.x + ((n.x + n.w / 2) / 1280) * canvasBox.w,
                           canvasBox.y + ((n.y + n.h / 2) / 720) * canvasBox.h);
    await new Promise((r) => setTimeout(r, 250));
  };

  // 2) 打开已开放的全科诊室面板
  await clickRoom("GP");
  const panelMeasure = await measure(page);
  const panelShot = await shotFor(page, SHOTS, vp, prefix ? `${prefix}_panel` : "panel");

  // 3) 改成未开放房间（检验室，能看到购买文案与关闭按钮），再截一张
  await page.click("#rp-close");
  await clickRoom("LB");
  const lockedMeasure = await measure(page);
  const lockedShot = await shotFor(page, SHOTS, vp, prefix ? `${prefix}_locked` : "locked");

  // 4) 只对候选做：全科扩到 3 工位，证明"扩容真的看得见"
  await page.click("#rp-close");
  let expandedShot = null, expandedInfo = null;
  const hasDebug = await page.evaluate(() => !!window.HOSP_DEBUG);
  if (hasDebug) {
    expandedInfo = await page.evaluate(() => {
      const D = window.HOSP_DEBUG;
      D.state.coins = 99999;
      D.api.doAction("slot", "GP");       // 1 → 2
      D.api.doAction("slot", "GP");       // 2 → 3
      D.api.doAction("slot", "PH");       // 药房 1 → 2
      for (let i = 0; i < 900; i++) D.api.tick();
      return {
        gpSlots: D.state.rooms.GP.slots,
        phSlots: D.state.rooms.PH.slots,
        gpSlotAnchors: [0, 1, 2].map(i => D.api.slotAnchor("GP", i, D.state.rooms.GP.slots)),
        gpWaitAnchors: [0, 1, 2].map(i => D.api.waitAnchor("GP", i)),
        servingAtOnce: D.state.patients.filter(p => p.state === "SERVE" && p.serveRoom === "GP")
          .map(p => [p.serveSlot, Math.round(p.x), Math.round(p.y)]),
      };
    });
    await clickRoom("GP");
    expandedShot = await shotFor(page, SHOTS, vp, prefix ? `${prefix}_expanded` : "expanded");
    await page.click("#rp-close");
  }

  report.push({ viewport: vp.name, main: base_measure, panel: panelMeasure, locked: lockedMeasure,
    expanded: expandedInfo, errs,
    shots: { main: mainShot, panel: panelShot, locked: lockedShot, expanded: expandedShot } });
  await page.close();
  return report;
}

// ---------------------------------------------------------------- 录屏
async function recordFlow(browser, base) {
  fs.rmSync(FRAMES, { recursive: true, force: true });
  fs.mkdirSync(FRAMES, { recursive: true });

  const page = await browser.newPage();
  await page.setViewport({ width: 844, height: 390, deviceScaleFactor: 1 });
  const errs = [];
  page.on("pageerror", (e) => errs.push(String(e.message)));

  const client = await page.createCDPSession();
  const frameFiles = [];
  let lastTs = null;
  client.on("Page.screencastFrame", async (f) => {
    const idx = frameFiles.length + 1;
    const name = `frame_${String(idx).padStart(5, "0")}.jpg`;
    fs.writeFileSync(path.join(FRAMES, name), Buffer.from(f.data, "base64"));
    frameFiles.push({ name, ts: f.metadata.timestamp, dt: lastTs === null ? 0.1 : Math.max(0.02, f.metadata.timestamp - lastTs) });
    lastTs = f.metadata.timestamp;
    try { await client.send("Page.screencastFrameAck", { sessionId: f.sessionId }); } catch (_) {}
  });

  await page.goto(`${base}/index.html`, { waitUntil: "load" });
  await page.evaluate(() => { try { localStorage.clear(); } catch (e) {} });
  await page.reload({ waitUntil: "load" });

  await client.send("Page.startScreencast", {
    format: "jpeg", quality: 72, maxWidth: 844, maxHeight: 390, everyNthFrame: 1,
  });

  const mark = (s) => out(`  [录屏] ${new Date().toISOString().slice(11, 19)}  ${s}`);
  const st = () => page.evaluate(() => ({
    coins: window.HOSP_DEBUG.state.coins,
    completed: window.HOSP_DEBUG.state.stats.completed,
    lb: window.HOSP_DEBUG.state.rooms.LB.unlocked,
    tr: window.HOSP_DEBUG.state.rooms.TR.unlocked,
    lbService: window.HOSP_DEBUG.state.stats.service.LB || 0,
    sim: Math.round(window.HOSP_DEBUG.state.simTick / 10),
  }));
  const clickRoomUI = async (rid, act) => {
    const box = await page.evaluate(() => { const r = document.getElementById("canvas").getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; });
    const n = await page.evaluate((r) => window.HOSP_DEBUG.api.roomRect(r), rid);
    await page.mouse.click(box.x + ((n.x + n.w / 2) / 1280) * box.w, box.y + ((n.y + n.h / 2) / 720) * box.h);
    await new Promise((r) => setTimeout(r, 350));
    await page.click(`#rp-body button[data-act="${act}"]`);
    await new Promise((r) => setTimeout(r, 450));
    await page.click("#rp-close");
    await new Promise((r) => setTimeout(r, 200));
  };

  mark("开始：打开页面、切到 4× 速度");
  await page.click("#btn-speed"); await page.click("#btn-speed");   // 1× → 2× → 4×
  await new Promise((r) => setTimeout(r, 1200));

  const deadline = Date.now() + 260000;
  const waitFor = async (pred, label, cap = 220000) => {
    const t0 = Date.now();
    while (Date.now() - t0 < cap && Date.now() < deadline) {
      const s = await st();
      if (pred(s)) return s;
      await new Promise((r) => setTimeout(r, 700));
    }
    mark(`超时未满足：${label}（当前 ${JSON.stringify(await st())}）`);
    return null;
  };

  mark("等待首位患者完成（自动诊疗产生收入）");
  await waitFor((s) => s.completed >= 1, "首例完成");

  mark("等待金币 ≥ 400，投币开放处置室");
  const atTr = await waitFor((s) => s.coins >= 400, "金币≥400");
  if (atTr) await clickRoomUI("TR", "unlock");
  mark("处置室状态：" + JSON.stringify(await st()));

  mark("等待金币 ≥ 1400，投币开放检验室");
  const atLb = await waitFor((s) => s.coins >= 1400, "金币≥1400", 240000);
  if (atLb) await clickRoomUI("LB", "unlock");
  mark("检验室状态：" + JSON.stringify(await st()));

  mark("等待检验室实际接待 ≥ 3 位患者");
  await waitFor((s) => s.lbService >= 3, "检验≥3", 120000);
  const final = await st();
  mark("结束：" + JSON.stringify(final));

  await new Promise((r) => setTimeout(r, 900));
  await client.send("Page.stopScreencast");
  await page.close();

  // 合成视频
  const concat = path.join(FRAMES, "list.txt");
  const total = frameFiles.reduce((s, f) => s + f.dt, 0) + frameFiles[frameFiles.length - 1].dt;
  fs.writeFileSync(concat, frameFiles.map((f) =>
    `file '${f.name}'\nduration ${f.dt.toFixed(4)}`).join("\n") + `\nfile '${frameFiles[frameFiles.length - 1].name}'\n`, "utf8");

  fs.mkdirSync(REC, { recursive: true });
  const mp4 = path.join(REC, "opening_to_lab_844x390.mp4");
  const res = spawnSync(FFMPEG, [
    "-y", "-f", "concat", "-safe", "0", "-i", concat,
    "-vsync", "vfr", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "24", "-preset", "medium",
    "-movflags", "+faststart", mp4,
  ], { encoding: "utf8" });
  if (res.status !== 0) out("  ffmpeg stderr:\n" + String(res.stderr).slice(-1500));
  return { frames: frameFiles.length, seconds: +total.toFixed(1), mp4, errs, final };
}

// ---------------------------------------------------------------- 主流程
async function main() {
  out("=".repeat(78));
  out("真实浏览器证据（puppeteer + 本机 Chromium，真实 index.html / 真实 CSS / 真实脚本）");
  out("=".repeat(78));
  fs.mkdirSync(SHOTS, { recursive: true });

  const A = await serve(ORIGIN);
  const B = await serve(CAND);
  out(`  修复前 origin_frozen 服务：http://127.0.0.1:${A.port}`);
  out(`  修复后 prototype     服务：http://127.0.0.1:${B.port}`);

  const browser = await puppeteer.launch({
    headless: true,
    args: ["--no-sandbox", "--disable-setuid-sandbox", "--hide-scrollbars", "--mute-audio"],
  });
  out(`  Chromium: ${await browser.version()}`);
  out("");

  const before = [], after = [];
  for (const vp of VIEWPORTS) {
    out(`── 视口 ${vp.name} ──`);
    await captureViewport(browser, `http://127.0.0.1:${A.port}`, "before", vp, before);
    await captureViewport(browser, `http://127.0.0.1:${B.port}`, "after", vp, after);
    const b = before[before.length - 1], a = after[after.length - 1];
    out(`  面板打开时右侧越界：  修复前 ${b.locked.panelOverflowRight}px  →  修复后 ${a.locked.panelOverflowRight}px`);
    out(`  页面横向溢出：        修复前 ${b.locked.overflowX}px  →  修复后 ${a.locked.overflowX}px`);
    out(`  画布宽高比（应为 1.7778）：修复前 ${b.locked.canvasAspect}  →  修复后 ${a.locked.canvasAspect}`);
    out(`  标题实际字号 CSS px： 修复前 ${b.locked.titleCssPxFromCanvas.toFixed(1)}  →  修复后 ${a.locked.titleCssPx.toFixed(1)}`);
    out(`  患者实际直径 CSS px： 修复前 ${b.locked.patientCssPxFromCanvas.toFixed(1)}  →  修复后 ${(a.locked.patientCssPx * 2).toFixed(1)}`);
    out(`  关闭按钮在视口内：    修复前 ${b.locked.closeBtnRect ? b.locked.closeBtnRect.inView : "n/a"}  →  修复后 ${a.locked.closeBtnRect ? a.locked.closeBtnRect.inView : "n/a"}`);
    out(`  购买按钮在视口内：    修复前 ${b.locked.unlockBtnRect ? b.locked.unlockBtnRect.inView : "n/a"}  →  修复后 ${a.locked.unlockBtnRect ? a.locked.unlockBtnRect.inView : "n/a"}`);
    if (a.expanded) {
      out(`  扩容可见性（候选）：全科 ${a.expanded.gpSlots} 工位 / 药房 ${a.expanded.phSlots} 工位`);
      out(`    全科三个工位锚点：${JSON.stringify(a.expanded.gpSlotAnchors)}`);
      out(`    全科三个等候位锚点：${JSON.stringify(a.expanded.gpWaitAnchors)}`);
      out(`    截图瞬间同时在岗：${JSON.stringify(a.expanded.servingAtOnce)}`);
    }
    out("");
  }

  out("── 连续开业到检验室运转录屏 ──");
  if (process.env.SKIP_RECORD) {
    out("  （SKIP_RECORD 已设置，本次跳过录屏）");
  } else {
    const rec = await recordFlow(browser, `http://127.0.0.1:${B.port}`);
    out(`  帧数 ${rec.frames}，时长 ${rec.seconds} 秒`);
    out(`  视频：${path.relative(HOSP, rec.mp4)}`);
    out(`  终局状态：${JSON.stringify(rec.final)}`);
    if (rec.errs.length) out("  页面错误：" + JSON.stringify(rec.errs));
    var recResult = rec;
  }

  await browser.close();
  A.server.close(); B.server.close();

  const json = { generatedAt: new Date().toISOString(), before, after, recording: recResult || null };
  fs.writeFileSync(path.join(ROOT, "logs", "browser_evidence.json"), JSON.stringify(json, null, 2), "utf8");
  fs.mkdirSync(path.dirname(LOG), { recursive: true });
  fs.writeFileSync(LOG, lines.join("\n") + "\n", "utf8");
  console.log("\n[写入] " + LOG);
}

main().catch((e) => { console.error(e); process.exit(1); });
