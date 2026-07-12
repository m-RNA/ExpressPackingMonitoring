import assert from 'node:assert/strict';
import test from 'node:test';
import { chromium } from 'playwright-core';

const baseUrl = process.env.EPM_AUTOMATION_BASE_URL;

test('isolated Web server supports search, playback and clip editor entry', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'zh-CN' });
    const page = await context.newPage();
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: '快递打包录像回放' }).waitFor());

    const search = page.getByPlaceholder('输入订单号关键词搜索');
    await search.fill('AUTO_WEB_001');
    await search.press('Enter');
    const article = page.locator('article').filter({ hasText: 'AUTO_WEB_001' });
    await assert.doesNotReject(() => article.waitFor());

    await article.getByRole('button', { name: '播放' }).click();
    await assert.doesNotReject(() => page.locator('#playerOverlay.active').waitFor());
    const source = await page.locator('#videoPlayer').getAttribute('src');
    assert.match(source ?? '', /\/api\/videos\/\d+\/play/);
    await page.keyboard.press('Escape');

    await article.getByRole('button', { name: '剪辑' }).click();
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
  } finally {
    await browser.close();
  }
});

test('Web UI follows browser language and persists an explicit override', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'en-US' });
    const page = await context.newPage();
    await page.addInitScript(() => {
      if (!localStorage.getItem('expressWebLanguage')) localStorage.setItem('expressWebLanguage', 'en-US');
    });
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: 'Packing Monitor Recordings' }).waitFor());
    assert.equal(await page.locator('html').getAttribute('lang'), 'en');
    await assert.doesNotReject(() => page.locator('#compatModeText').filter({ hasText: 'Non-H.264 videos are automatically transcoded to H.264' }).waitFor());

    const search = page.getByPlaceholder('Search by order number');
    await search.fill('AUTO_WEB_001');
    await search.press('Enter');
    const article = page.locator('article').filter({ hasText: 'AUTO_WEB_001' });
    await assert.doesNotReject(() => article.waitFor());
    await assert.doesNotReject(() => article.getByRole('button', { name: 'Play' }).waitFor());
    await assert.doesNotReject(() => article.getByRole('button', { name: 'Download' }).waitFor());
    assert.doesNotMatch(await article.innerText(), /发货|退货|文件存在|文件丢失|播放|下载/);
    assert.equal(await page.locator('#startDate').getAttribute('type'), 'text');
    assert.equal(await page.locator('#startDate').getAttribute('placeholder'), 'YYYY-MM-DD');

    await page.evaluate(() => renderPagination(2, 3));
    await assert.doesNotReject(() => page.getByRole('button', { name: 'Previous' }).waitFor());
    await assert.doesNotReject(() => page.getByRole('button', { name: 'Next' }).waitFor());

    await page.goto(`${baseUrl.replace(/\/$/, '')}/kuaidizs-install-guide`, { waitUntil: 'networkidle' });
    const guideText = await page.locator('.steps').innerText();
    assert.doesNotMatch(guideText, /[\u3400-\u9fff]/);

    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await page.locator('select[aria-label="Display language"]').selectOption('zh-Hans');
    await page.waitForLoadState('networkidle');
    await assert.doesNotReject(() => page.getByRole('heading', { name: '快递打包录像回放' }).waitFor());
    assert.equal(await page.evaluate(() => localStorage.getItem('expressWebLanguage')), 'zh-Hans');
  } finally {
    await browser.close();
  }
});
