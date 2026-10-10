const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const puppeteer = require('/Users/kker/.workbuddy/binaries/node/workspace/node_modules/puppeteer');
const root = __dirname;
const types = { '.html': 'text/html', '.css': 'text/css', '.mjs': 'text/javascript' };
const server = http.createServer((req, res) => {
  const target = path.join(root, decodeURIComponent(req.url === '/' ? 'index.html' : req.url.split('?')[0]));
  if (!target.startsWith(root) || !fs.existsSync(target)) { res.writeHead(404); return res.end(); }
  res.setHeader('Content-Type', types[path.extname(target)] || 'application/octet-stream');
  fs.createReadStream(target).pipe(res);
});

(async () => {
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const browser = await puppeteer.launch({ headless: true, args: ['--no-sandbox'] });
  try {
    for (const viewport of [{ width: 1280, height: 720 }, { width: 844, height: 390 }, { width: 667, height: 375 }]) {
      const page = await browser.newPage();
      await page.setViewport(viewport);
      const errors = [];
      page.on('pageerror', (error) => errors.push(error.message));
      await page.goto(`http://127.0.0.1:${server.address().port}`, { waitUntil: 'domcontentloaded' });
      await page.waitForSelector('.clinic-map');
      assert.match(await page.$eval('#clock', (el) => el.textContent), /^08:\d\d$/);
      assert.equal(await page.$eval('.clinic-map', (el) => getComputedStyle(el).display), 'block');
      await page.click('#openLab');
      await new Promise((resolve) => setTimeout(resolve, 320));
      assert.ok((await page.$eval('#labPanel', (el) => getComputedStyle(el).transform)).includes('matrix(1, 0, 0, 1'));
      if (viewport.width < 950) {
        const panelRatio = await page.$eval('#labPanel', (el) => el.getBoundingClientRect().width / innerWidth);
        assert.ok(panelRatio >= 0.7 && panelRatio <= 0.8, `phone panel width ${panelRatio} at ${viewport.width}px`);
        assert.ok(await page.$eval('#labPanel', (el) => el.getBoundingClientRect().left >= 0), 'phone lab panel must stay inside viewport');
      }
      if (viewport.width === 844) await page.screenshot({ path: '/tmp/h5-clinic-panel.png' });
      await page.click('[data-plan="risk-priority"]');
      assert.match(await page.$eval('#dispatchImpact', (el) => el.textContent), /风险较高/);
      await page.click('#closeLab');
      await page.click('#pauseBtn');
      assert.equal(await page.$eval('#pauseBtn', (el) => el.getAttribute('aria-label')), '继续');
      assert.deepEqual(errors, []);
      console.log(`PASS ${viewport.width}x${viewport.height}: map, lab panel, dispatch choice, pause; no JS errors`);
      await page.close();
    }
  } finally {
    await browser.close();
    server.close();
  }
})().catch((error) => { console.error(error); process.exitCode = 1; server.close(); });
