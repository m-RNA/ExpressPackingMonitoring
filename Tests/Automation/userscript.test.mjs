import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';

const repoRoot = path.resolve(import.meta.dirname, '..', '..');
const scriptPath = path.join(repoRoot, 'Scripts', '快递助手订单推送.user.js');
const source = fs.readFileSync(scriptPath, 'utf8');

function between(startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start >= 0 && end > start, `Cannot extract ${startMarker}`);
  return source.slice(start, end);
}

function createLeaseWorker(store, token, closed) {
  const context = {
    REFUND_WORKER_HEARTBEAT_KEY: 'heartbeat',
    REFUND_WORKER_TOKEN: token,
    REFUND_WORKER_STALE_MS: 10 * 60 * 1000,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    delay: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
    document: { title: '' },
    window: { close: () => closed.push(token) },
    location: { replace: () => {} },
    setTimeout,
    Math,
    Date
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getRefundWorkerHeartbeat()', '    function getApiUrl()') +
      ';globalThis.claimLease=claimRefundWorkerLease;',
    context);
  return context;
}

function createWorkerManager({ reachable, savedTabs = {} }) {
  const store = new Map();
  const opened = [];
  const currentTab = {};
  const context = {
    REFUND_WORKER_HEARTBEAT_KEY: 'heartbeat',
    REFUND_WORKER_OPEN_LOCK_KEY: 'open-lock',
    REFUND_WORKER_TOKEN: 'ordinary-page',
    REFUND_WORKER_STALE_MS: 10 * 60 * 1000,
    REFUND_WORKER_OPEN_COOLDOWN_MS: 10 * 60 * 1000,
    IS_REFUND_WORKER: false,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    GM_getTab: callback => callback(currentTab),
    GM_saveTab: (tab, callback) => callback?.(),
    GM_getTabs: callback => callback(savedTabs),
    GM_openInTab: url => opened.push(url),
    getMonitorAddress: () => ({ host: '192.168.2.11', port: 5280 }),
    canConnectMonitor: async () => reachable,
    buildRefundWorkerUrl: () => 'https://p4.kuaidizs.cn/?epm_refund_worker=1',
    delay: () => Promise.resolve(),
    document: { querySelector: () => ({}) },
    window: { close() {}, addEventListener() {} },
    location: { replace() {} },
    setTimeout,
    Math,
    Date,
    Object,
    Promise
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getRefundWorkerHeartbeat()', '    function getApiUrl()') +
      ';globalThis.ensureWorker=ensureRefundWorker;globalThis.maintainWorker=maintainRefundWorker;',
    context);
  return { context, opened };
}

test('concurrent refund workers keep exactly one lease owner', async () => {
  const store = new Map();
  const closed = [];
  const workers = Array.from({ length: 8 }, (_, index) => createLeaseWorker(store, `worker-${index}`, closed));
  const results = await Promise.all(workers.map(worker => worker.claimLease()));
  assert.equal(results.filter(Boolean).length, 1);
  assert.equal(closed.length, 7);
});

test('fresh lease is retained and stale lease can recover', async () => {
  const store = new Map([['heartbeat', { token: 'owner', time: Date.now() }]]);
  const closed = [];
  assert.equal(await createLeaseWorker(store, 'new-worker', closed).claimLease(), false);
  assert.deepEqual(store.get('heartbeat').token, 'owner');

  store.set('heartbeat', { token: 'stale-owner', time: Date.now() - 10 * 60 * 1000 - 1 });
  assert.equal(await createLeaseWorker(store, 'replacement', closed).claimLease(), true);
  assert.equal(store.get('heartbeat').token, 'replacement');
});

test('offline monitor never opens a refund worker', async () => {
  const { context, opened } = createWorkerManager({ reachable: false });
  await context.maintainWorker();
  assert.equal(opened.length, 0);
});

test('saved refund worker tab prevents duplicate even with stale heartbeat', async () => {
  const { context, opened } = createWorkerManager({
    reachable: true,
    savedTabs: { existing: { epmRefundWorker: true } }
  });
  await context.ensureWorker(false, true);
  assert.equal(opened.length, 0);
});

function createDiscoveryContext(initialStore, reachableHosts, installAddress = '') {
  const store = initialStore instanceof Map ? initialStore : new Map(Object.entries(initialStore));
  let requestCount = 0;
  const document = {
    createElement: () => ({ style: {}, remove() {} }),
    body: { appendChild() {} }
  };
  const context = {
    DEFAULT_HOST: '127.0.0.1', DEFAULT_PORT: 5280, DEFAULT_ADDRESS: '127.0.0.1:5280',
    INSTALL_MONITOR_ADDRESS: installAddress,
    DISCOVERY_DONE_KEY: 'monitor_auto_discovery_done',
    DISCOVERY_LAST_ATTEMPT_KEY: 'monitor_auto_discovery_last_attempt',
    DISCOVERY_LOCK_KEY: 'monitor_auto_discovery_lock',
    DISCOVERY_LOCK_MS: 30000,
    DISCOVERY_RETRY_DELAY_MS: 1,
    DISCOVERY_TIMEOUT: 1,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    showNotification: () => {},
    getStorageUrl: (host, port) => `http://${host}:${port}/api/storage`,
    GM_xmlhttpRequest: options => {
      requestCount += 1;
      const host = new URL(options.url).hostname;
      queueMicrotask(() => options.onload({ status: reachableHosts.has(host) ? 200 : 503 }));
    },
    window: {}, document, setTimeout, clearTimeout, URL, Promise, Set, Array, String, Number, Boolean, Math, Date
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getBaseUrl(', '    // 专用工作页不显示业务菜单') +
      ';globalThis.findMonitor=findMonitorAddress;globalThis.ensureMonitor=ensureMonitorAddress;globalThis.shouldDiscover=shouldAttemptMonitorDiscovery;',
    context);
  return { context, store, getRequestCount: () => requestCount };
}

test('installed monitor address replaces an offline saved address without scanning the subnet', async () => {
  const { context, store } = createDiscoveryContext(
    { monitor_address: '192.168.31.250:5280' },
    new Set(['192.168.31.10']),
    '192.168.31.10:5280');
  assert.equal(await context.findMonitor(false), '192.168.31.10:5280');
  assert.equal(store.get('monitor_address'), '192.168.31.10:5280');
});

test('address lookup probes only installed saved and local addresses', async () => {
  const { context, getRequestCount } = createDiscoveryContext(
    { monitor_address: '192.168.31.250:5280' },
    new Set(),
    '192.168.31.10:5280');
  assert.equal(await context.findMonitor(false), '');
  assert.equal(getRequestCount(), 4);
});

test('failed discovery retries after backoff and recovers when monitor comes online', async () => {
  const reachableHosts = new Set();
  const { context, store } = createDiscoveryContext({}, reachableHosts, '192.168.31.10:5280');

  assert.equal(await context.ensureMonitor(true), false);
  assert.equal(store.get('monitor_auto_discovery_done'), true);
  reachableHosts.add('192.168.31.10');
  await new Promise(resolve => setTimeout(resolve, 2));

  assert.equal(await context.ensureMonitor(false), true);
  assert.equal(store.get('monitor_address'), '192.168.31.10:5280');
});

test('discovery backoff prevents repeated full scans before retry time', () => {
  assert.equal(
    createDiscoveryContext({}, new Set()).context.shouldDiscover(false, true, 1000, 1000),
    false);
  assert.equal(
    createDiscoveryContext({}, new Set()).context.shouldDiscover(false, true, 1000, 1001),
    true);
});

test('shared discovery lock prevents heartbeat tabs from scanning concurrently', async () => {
  const store = new Map();
  const first = createDiscoveryContext(store, new Set(), ['192.168.31']);
  const second = createDiscoveryContext(store, new Set(), ['192.168.31']);
  const firstScan = first.context.ensureMonitor(true);
  const secondResult = await second.context.ensureMonitor(true);
  await firstScan;
  assert.equal(secondResult, false);
  assert.equal(second.getRequestCount(), 0);
  assert.ok(first.getRequestCount() > 0);
});

function createConnectionHeartbeatContext(status = 200) {
  const store = new Map();
  const requests = [];
  const intervals = [];
  let discoveryCalls = 0;
  const context = {
    CONNECTION_CLIENT_ID_KEY: 'connection_client_id',
    CONNECTION_HEARTBEAT_INTERVAL_MS: 15000,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    getConnectionHeartbeatUrl: () => 'http://192.168.1.20:5280/api/connections/heartbeat',
    requestMonitor: async (method, url, data, timeout) => {
      requests.push({ method, url, data, timeout });
      return { status, body: {} };
    },
    ensureMonitorAddress: async () => { discoveryCalls += 1; return false; },
    setInterval: (callback, delay) => { intervals.push({ callback, delay }); return intervals.length; },
    Math,
    Date,
    String,
    Promise
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getConnectionClientId()', '    function delay(') +
      ';globalThis.getClientId=getConnectionClientId;globalThis.sendHeartbeat=sendConnectionHeartbeat;globalThis.startHeartbeat=startConnectionHeartbeat;',
    context);
  return { context, store, requests, intervals, getDiscoveryCalls: () => discoveryCalls };
}

test('userscript heartbeat keeps one persistent id across tabs', async () => {
  const first = createConnectionHeartbeatContext();
  const id = first.context.getClientId();
  assert.equal(first.context.getClientId(), id);
  assert.equal(first.store.get('connection_client_id'), id);
  assert.match(id, /^userscript-/);

  await first.context.sendHeartbeat();
  assert.equal(first.requests.length, 1);
  assert.equal(first.requests[0].data.clientId, id);
  assert.equal(first.requests[0].data.clientType, 'userscript');
});

test('userscript heartbeat uses 15 second interval and recovery respects discovery helper', async () => {
  const failed = createConnectionHeartbeatContext(0);
  failed.context.startHeartbeat();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(failed.intervals.length, 1);
  assert.equal(failed.intervals[0].delay, 15000);
  assert.equal(failed.getDiscoveryCalls(), 1);
});
