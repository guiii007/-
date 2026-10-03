const fs = require('fs');
const vm = require('vm');
const assert = require('assert/strict');
const script = fs.readFileSync(__dirname + '/browser-extension/background.js', 'utf8');
async function runCase({ result, scriptError, responseOK = true }) {
  let click, install, created, payload, notification, target;
  const context = {
    importScripts() {}, CONTENT_MOVER_TOKEN: 'test-token',
    chrome: {
      runtime: { onInstalled: { addListener(fn) { install = fn; } } },
      contextMenus: { create(value) { created = value; }, onClicked: { addListener(fn) { click = fn; } } },
      scripting: { async executeScript(value) { target = value.target; if (scriptError) throw Error('restricted page'); return [{ result }]; } },
      notifications: { create(value) { notification = value; } }
    },
    async fetch(url, options) { payload = { url, options, json: JSON.parse(options.body) }; return { ok: responseOK, status: responseOK ? 202 : 403 }; }
  };
  vm.runInNewContext(script, context);
  install(); assert.equal(created.title, '内容迁移'); assert.equal(created.contexts[0], 'selection');
  await click({ menuItemId: 'content-mover', selectionText: 'normalized text', pageUrl: 'https://example.test/page', frameUrl: 'https://example.test/frame', frameId: 7 }, { id: 123, title: '页面标题' });
  assert.equal(payload.url, 'http://127.0.0.1:19339/capture');
  assert.equal(payload.options.headers.Authorization, 'Bearer test-token');
  assert.equal(payload.json.url, 'https://example.test/page');
  assert.equal(payload.json.frameUrl, 'https://example.test/frame');
  assert.equal(target.tabId, 123); assert.equal(target.frameIds[0], 7);
  return { payload, notification };
}
(async () => {
  const original = '  中文\nsecond\tline  😀';
  const exact = await runCase({ result: original });
  assert.equal(exact.payload.json.text, original);
  assert.equal(exact.payload.json.fidelity, '网页选区原文');
  const fallback = await runCase({ scriptError: true });
  assert.equal(fallback.payload.json.text, 'normalized text');
  assert.match(fallback.payload.json.fidelity, /核对换行/);
  const failed = await runCase({ result: original, responseOK: false });
  assert.equal(failed.notification.title, '内容迁移未完成');
  console.log('PASS: extension menu; exact selection; frame targeting; source URLs; authenticated loopback request; restricted-page fallback; failure notification.');
})().catch(error => { console.error(error); process.exitCode = 1; });
