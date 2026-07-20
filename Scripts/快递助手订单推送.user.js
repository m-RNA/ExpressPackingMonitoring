// ==UserScript==
// @name         订单备注播报插件
// @namespace    https://github.com/ExpressPackingMonitoring
// @version      2.9
// @description  从快递助手批量打印页面提取订单备注和打印后退款状态，同时发送到已配对的电脑和手机
// @author       ExpressPackingMonitoring
// @icon         https://raw.githubusercontent.com/m-RNA/ExpressPackingMonitoring/main/ExpressPackingMonitoring/app.ico
// @match        *://p4.kuaidizs.cn/*
// @match        *://kuaidizs.cn/*
// @match        *://*.kuaidizs.cn/*
// @connect      localhost
// @connect      127.0.0.1
// @grant        GM_xmlhttpRequest
// @grant        GM_registerMenuCommand
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_openInTab
// @grant        GM_getTab
// @grant        GM_saveTab
// @grant        GM_getTabs
// @noframes
// @run-at       document-idle
// ==/UserScript==

(function () {
    'use strict';

    // ============ 配置 ============
    const DEFAULT_HOST = '127.0.0.1';
    const DEFAULT_PORT = 5280;
    const DEFAULT_ADDRESS = `${DEFAULT_HOST}:${DEFAULT_PORT}`;
    const INSTALL_MONITOR_ADDRESSES = [];
    const INSTALL_PRIMARY_MONITOR_ADDRESS = '';
    const MONITOR_ADDRESSES_KEY = 'monitor_addresses';
    const INSTALLED_MONITOR_ADDRESSES_KEY = 'installed_monitor_addresses';
    const INSTALLED_PRIMARY_MONITOR_ADDRESS_KEY = 'installed_primary_monitor_address';
    const MAX_MONITOR_ADDRESSES = 8;
    const DISCOVERY_DONE_KEY = 'monitor_auto_discovery_done';
    const DISCOVERY_LAST_ATTEMPT_KEY = 'monitor_auto_discovery_last_attempt';
    const DISCOVERY_LOCK_KEY = 'monitor_auto_discovery_lock';
    const DISCOVERY_LOCK_MS = 30000;
    const DISCOVERY_RETRY_DELAY_MS = 60 * 1000;
    const DISCOVERY_TIMEOUT = 700;
    const ORDER_LOOKUP_RECONNECT_MS = 250;
    const PRINTED_REFUND_QUERY_TIMEOUT_MS = 6000;
    const PRINTED_REFUND_STABLE_MS = 500;
    const PRINTED_REFUND_FILTER_COOLDOWN_MS = 30000;
    const USER_ACTIVITY_IDLE_MS = 30000;
    const REFUND_WORKER_PARAM = 'epm_refund_worker';
    const REFUND_WORKER_HEARTBEAT_KEY = 'refund_worker_heartbeat';
    const REFUND_WORKER_OPEN_LOCK_KEY = 'refund_worker_open_lock';
    const REFUND_WORKER_HEARTBEAT_INTERVAL_MS = 30000;
    const REFUND_WORKER_RECHECK_INTERVAL_MS = 30000;
    // Chrome 可能暂停长时间处于后台的标签页，不应因短时无心跳反复新建工作页。
    const REFUND_WORKER_STALE_MS = 10 * 60 * 1000;
    const REFUND_WORKER_OPEN_COOLDOWN_MS = 10 * 60 * 1000;
    const CONNECTION_CLIENT_ID_KEY = 'connection_client_id';
    const CONNECTION_HEARTBEAT_INTERVAL_MS = 15000;
    const IS_REFUND_WORKER = new URL(location.href).searchParams.get(REFUND_WORKER_PARAM) === '1';
    const REFUND_WORKER_TOKEN = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const CHANGELOG = 'v2.9：订单同时推送到已配对的电脑和手机';
    const DEBUG_LOG = false;

    let lastUserActivityAt = Date.now();
    function recordUserActivity(event) {
        if (event.isTrusted) lastUserActivityAt = Date.now();
    }
    document.addEventListener('mousemove', recordUserActivity, { capture: true, passive: true });
    document.addEventListener('pointerdown', recordUserActivity, { capture: true, passive: true });
    document.addEventListener('wheel', recordUserActivity, { capture: true, passive: true });
    document.addEventListener('touchstart', recordUserActivity, { capture: true, passive: true });
    document.addEventListener('keydown', recordUserActivity, true);

    function isUserActivelyUsingPage(now) {
        return document.visibilityState === 'visible' &&
            document.hasFocus() &&
            now - lastUserActivityAt < USER_ACTIVITY_IDLE_MS;
    }

    function debugLog(...args) {
        if (DEBUG_LOG) console.log(...args);
    }

    function buildRefundWorkerUrl() {
        const url = new URL(location.href);
        url.searchParams.set(REFUND_WORKER_PARAM, '1');
        return url.href;
    }

    function getRefundWorkerHeartbeat() {
        const value = GM_getValue(REFUND_WORKER_HEARTBEAT_KEY, 0);
        return typeof value === 'object' && value
            ? { token: String(value.token || ''), time: Number(value.time || 0) }
            : { token: '', time: Number(value || 0) };
    }

    function writeRefundWorkerHeartbeat() {
        GM_setValue(REFUND_WORKER_HEARTBEAT_KEY, { token: REFUND_WORKER_TOKEN, time: Date.now() });
    }

    function releaseRefundWorkerLease() {
        const heartbeat = getRefundWorkerHeartbeat();
        if (heartbeat.token === REFUND_WORKER_TOKEN) {
            GM_setValue(REFUND_WORKER_HEARTBEAT_KEY, 0);
        }
    }

    function saveRefundWorkerTabIdentity(isWorker) {
        return new Promise(resolve => {
            GM_getTab(tab => {
                tab.epmRefundWorker = isWorker;
                tab.epmRefundWorkerToken = isWorker ? REFUND_WORKER_TOKEN : '';
                GM_saveTab(tab, resolve);
            });
        });
    }

    function hasOpenRefundWorkerTab() {
        return new Promise(resolve => {
            GM_getTabs(tabs => {
                resolve(Object.values(tabs || {}).some(tab => tab?.epmRefundWorker === true));
            });
        });
    }

    function closeDuplicateRefundWorker() {
        document.title = '【重复的退款核验页】正在关闭';
        window.close();
        setTimeout(() => location.replace('about:blank'), 300);
    }

    async function claimRefundWorkerLease() {
        const heartbeat = getRefundWorkerHeartbeat();
        if (heartbeat.time > 0 && Date.now() - heartbeat.time < REFUND_WORKER_STALE_MS &&
            heartbeat.token !== REFUND_WORKER_TOKEN) {
            closeDuplicateRefundWorker();
            return false;
        }

        writeRefundWorkerHeartbeat();
        await delay(100 + Math.floor(Math.random() * 150));
        const ownedHeartbeat = getRefundWorkerHeartbeat();
        if (ownedHeartbeat.token !== REFUND_WORKER_TOKEN) {
            closeDuplicateRefundWorker();
            return false;
        }
        return true;
    }

    async function startRefundWorkerHeartbeat() {
        if (!await claimRefundWorkerLease()) return false;
        await saveRefundWorkerTabIdentity(true);

        window.addEventListener('pagehide', event => {
            if (!event.persisted) releaseRefundWorkerLease();
        });
        setInterval(() => {
            const heartbeat = getRefundWorkerHeartbeat();
            if (heartbeat.token && heartbeat.token !== REFUND_WORKER_TOKEN) {
                closeDuplicateRefundWorker();
                return;
            }
            writeRefundWorkerHeartbeat();
        }, REFUND_WORKER_HEARTBEAT_INTERVAL_MS);
        const workerTitle = '【退款核验专用】请勿操作';
        const applyWorkerIdentity = () => {
            if (document.title !== workerTitle) document.title = workerTitle;
        };
        applyWorkerIdentity();
        const titleElement = document.querySelector('title');
        if (titleElement) {
            new MutationObserver(applyWorkerIdentity).observe(titleElement, { childList: true, subtree: true, characterData: true });
        }

        const overlay = document.createElement('div');
        overlay.setAttribute('data-epm-refund-worker-overlay', '');
        overlay.innerHTML = `
            <div style="width:min(560px,calc(100vw - 48px));padding:48px 40px;border:1px solid rgba(255,255,255,.22);border-radius:20px;background:rgba(15,23,42,.72);box-shadow:0 24px 80px rgba(0,0,0,.38);text-align:center;">
                <img src="https://raw.githubusercontent.com/m-RNA/ExpressPackingMonitoring/main/ExpressPackingMonitoring/app.ico" alt="" style="width:88px;height:88px;margin-bottom:24px;" />
                <div style="font-size:34px;line-height:1.35;font-weight:800;letter-spacing:1px;">退款核验专用工作页</div>
                <div style="margin-top:18px;font-size:22px;line-height:1.6;font-weight:700;color:#fecaca;">请勿操作或关闭此页面</div>
                <div style="margin-top:22px;font-size:15px;line-height:1.8;color:#cbd5e1;">此页面由快递打包监控自动管理<br />用于在后台核验打印后退款订单</div>
            </div>`;
        Object.assign(overlay.style, {
            position: 'fixed', inset: '0', zIndex: '2147483647',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            background: 'linear-gradient(145deg,rgba(127,29,29,.64) 0%,rgba(153,27,27,.6) 42%,rgba(69,10,10,.68) 100%)',
            color: '#fff', fontFamily: 'Microsoft YaHei, sans-serif',
            pointerEvents: 'auto', userSelect: 'none'
        });
        document.body.appendChild(overlay);
        return true;
    }

    async function ensureRefundWorker(force, monitorReachable) {
        if (IS_REFUND_WORKER) return;
        if (!force && !document.querySelector('tr.packageItem, select.extendSearchList')) return;
        if (!force && monitorReachable !== true) return;

        const now = Date.now();
        const heartbeat = getRefundWorkerHeartbeat();
        if (!force && heartbeat.time > 0 && now - heartbeat.time < REFUND_WORKER_STALE_MS) return;
        if (!force && await hasOpenRefundWorkerTab()) return;

        const currentLock = GM_getValue(REFUND_WORKER_OPEN_LOCK_KEY, null);
        if (!force && currentLock && now - Number(currentLock.time || 0) < REFUND_WORKER_OPEN_COOLDOWN_MS) return;

        const token = `${now}-${Math.random().toString(36).slice(2)}`;
        GM_setValue(REFUND_WORKER_OPEN_LOCK_KEY, { token, time: now });
        await delay(100);
        const ownedLock = GM_getValue(REFUND_WORKER_OPEN_LOCK_KEY, null);
        if (!ownedLock || ownedLock.token !== token) return;

        // 默认追加到标签栏末尾，不与用户正在操作的打印页并排插入。
        GM_openInTab(buildRefundWorkerUrl(), { active: false, setParent: false });
        debugLog('[打包监控] 已在后台打开退款核验工作页');
    }

    let refundWorkerMaintenancePromise = null;
    function maintainRefundWorker(monitorReachable) {
        if (IS_REFUND_WORKER) return Promise.resolve();
        if (refundWorkerMaintenancePromise) return refundWorkerMaintenancePromise;

        refundWorkerMaintenancePromise = (async () => {
            if (monitorReachable === undefined) {
                const heartbeat = getRefundWorkerHeartbeat();
                if (heartbeat.time > 0 && Date.now() - heartbeat.time < REFUND_WORKER_STALE_MS) return;
                if (await hasOpenRefundWorkerTab()) return;

                const address = getMonitorAddress();
                monitorReachable = await canConnectMonitor(address.host, address.port);
            }
            await ensureRefundWorker(false, monitorReachable);
        })().finally(() => { refundWorkerMaintenancePromise = null; });
        return refundWorkerMaintenancePromise;
    }

    function getApiUrl() { return `${getBaseUrl(getMonitorAddressText())}/api/orderinfo`; }
    function getOrderLookupPendingUrl() { return `${getBaseUrl(getMonitorAddressText())}/api/order-lookup/pending`; }
    function getOrderLookupResultUrl() { return `${getBaseUrl(getMonitorAddressText())}/api/order-lookup/result`; }
    function getConnectionHeartbeatUrl() { return `${getBaseUrl(getMonitorAddressText())}/api/connections/heartbeat`; }
    function getStorageUrl(host, port) { return `${getBaseUrl(host, port)}/api/storage`; }
    function getBaseUrl(host, port) {
        const address = normalizeAddress(host, port);
        return `http://${address.host}:${address.port}`;
    }
    function normalizeAddress(host, port) {
        let value = String(host || '').trim()
            .replace(/^https?:\/\//i, '')
            .replace(/\/+$/g, '');
        const parts = value.split(':');
        let normalizedHost = parts[0] || DEFAULT_HOST;
        if (/^127(?:\.\d{1,3}){3}$/.test(normalizedHost)) {
            normalizedHost = DEFAULT_HOST;
        }
        if (!isAllowedMonitorHost(normalizedHost)) {
            normalizedHost = DEFAULT_HOST;
        }
        const parsedPort = Number(parts[1] || port || DEFAULT_PORT);
        return {
            host: normalizedHost,
            port: Number.isInteger(parsedPort) && parsedPort > 0 && parsedPort <= 65535 ? parsedPort : DEFAULT_PORT
        };
    }
    function isAllowedMonitorHost(host) {
        const value = String(host || '').trim().toLowerCase();
        if (value === 'localhost') return true;

        const match = value.match(/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/);
        if (!match) return false;
        const octets = match.slice(1).map(Number);
        if (octets.some(octet => octet < 0 || octet > 255)) return false;
        return octets[0] === 10 ||
            (octets[0] === 172 && octets[1] >= 16 && octets[1] <= 31) ||
            (octets[0] === 192 && octets[1] === 168) ||
            (octets[0] === 169 && octets[1] === 254);
    }
    function formatAddress(address) {
        const normalized = normalizeAddress(address.host, address.port);
        return `${normalized.host}:${normalized.port}`;
    }
    function normalizeAddressList(values) {
        const result = [];
        for (const value of Array.isArray(values) ? values : []) {
            const address = normalizeAddress(value, DEFAULT_PORT);
            const text = formatAddress(address);
            if (!result.includes(text)) result.push(text);
            if (result.length >= MAX_MONITOR_ADDRESSES) break;
        }
        return result;
    }
    function getPairedMonitorAddresses() {
        const stored = GM_getValue(MONITOR_ADDRESSES_KEY, []);
        const candidates = Array.isArray(stored) ? stored.slice() : [];
        const current = GM_getValue('monitor_address', '');
        if (current) candidates.push(current);

        const legacyHost = GM_getValue('monitor_host', '');
        const legacyPort = GM_getValue('monitor_port', '');
        if (legacyHost || legacyPort) candidates.push(formatAddress(normalizeAddress(legacyHost || DEFAULT_HOST, legacyPort || DEFAULT_PORT)));
        candidates.push(...INSTALL_MONITOR_ADDRESSES);
        if (candidates.length === 0) candidates.push(DEFAULT_ADDRESS);
        return normalizeAddressList(candidates);
    }
    function getMonitorAddressText() {
        const storedAddress = GM_getValue('monitor_address', '');
        if (storedAddress) return formatAddress(normalizeAddress(storedAddress, DEFAULT_PORT));
        return getPairedMonitorAddresses()[0] || DEFAULT_ADDRESS;
    }
    function getMonitorAddress() {
        return normalizeAddress(getMonitorAddressText(), DEFAULT_PORT);
    }
    function setActiveMonitorAddress(host, port) {
        const address = normalizeAddress(host, port);
        const text = formatAddress(address);
        GM_setValue('monitor_address', text);
        GM_setValue('monitor_host', address.host);
        GM_setValue('monitor_port', address.port);
        GM_setValue(DISCOVERY_DONE_KEY, true);
        return address;
    }

    function saveMonitorAddress(host, port) {
        const address = normalizeAddress(host, port);
        const text = formatAddress(address);
        GM_setValue(MONITOR_ADDRESSES_KEY, normalizeAddressList([text, ...getPairedMonitorAddresses()]));
        return setActiveMonitorAddress(address.host, address.port);
    }

    function applyInstalledMonitorAddresses() {
        const installed = normalizeAddressList(INSTALL_MONITOR_ADDRESSES);
        if (installed.length === 0) return;

        const previousValue = GM_getValue(INSTALLED_MONITOR_ADDRESSES_KEY, []);
        const previous = normalizeAddressList(Array.isArray(previousValue) ? previousValue : []);
        const installedPrimary = INSTALL_PRIMARY_MONITOR_ADDRESS
            ? formatAddress(normalizeAddress(INSTALL_PRIMARY_MONITOR_ADDRESS, DEFAULT_PORT))
            : installed[0];
        const previousPrimary = String(GM_getValue(INSTALLED_PRIMARY_MONITOR_ADDRESS_KEY, '') || '');
        const legacyInstalled = GM_getValue('installed_monitor_address', '');
        if (legacyInstalled) previous.push(formatAddress(normalizeAddress(legacyInstalled, DEFAULT_PORT)));
        if (JSON.stringify(previous) === JSON.stringify(installed) && previousPrimary === installedPrimary) return;

        const retained = getPairedMonitorAddresses().filter(address => !previous.includes(address));
        const addresses = normalizeAddressList([...installed, ...retained]);
        GM_setValue(MONITOR_ADDRESSES_KEY, addresses);
        GM_setValue(INSTALLED_MONITOR_ADDRESSES_KEY, installed);
        GM_setValue(INSTALLED_PRIMARY_MONITOR_ADDRESS_KEY, installedPrimary || '');
        GM_setValue('installed_monitor_address', '');

        if (installedPrimary && addresses.includes(installedPrimary)) {
            const active = normalizeAddress(installedPrimary, DEFAULT_PORT);
            setActiveMonitorAddress(active.host, active.port);
        } else if (!addresses.includes(getMonitorAddressText())) {
            const active = normalizeAddress(addresses[0], DEFAULT_PORT);
            setActiveMonitorAddress(active.host, active.port);
        }
    }

    function parseJsonResponse(text) {
        try { return JSON.parse(text || '{}'); } catch (e) { return {}; }
    }

    function gmGet(url, timeout) {
        return new Promise(resolve => {
            GM_xmlhttpRequest({
                method: 'GET',
                url,
                timeout,
                onload: res => resolve({
                    ok: res.status >= 200 && res.status < 300,
                    response: parseJsonResponse(res.responseText)
                }),
                onerror: () => resolve({ ok: false, response: {} }),
                ontimeout: () => resolve({ ok: false, response: {} })
            });
        });
    }

    async function canConnectMonitor(host, port) {
        const result = await gmGet(getStorageUrl(host, port), DISCOVERY_TIMEOUT);
        return result.ok && result.response?.service !== 'packingproof-mobile';
    }

    async function findMonitorAddress(showProgress) {
        GM_setValue(DISCOVERY_LAST_ATTEMPT_KEY, Date.now());
        const saved = getMonitorAddress();
        const directCandidates = normalizeAddressList([
            formatAddress(saved),
            ...getPairedMonitorAddresses(),
            ...INSTALL_MONITOR_ADDRESSES,
            `127.0.0.1:${saved.port}`,
            `localhost:${saved.port}`
        ]).map(value => normalizeAddress(value, DEFAULT_PORT));

        for (const address of directCandidates) {
            if (showProgress) showNotification(`正在连接 ${formatAddress(address)}`);
            if (await canConnectMonitor(address.host, address.port)) {
                const found = saveMonitorAddress(address.host, address.port);
                return `${found.host}:${found.port}`;
            }
        }

        GM_setValue(DISCOVERY_DONE_KEY, true);
        return '';
    }

    let monitorDiscoveryPromise = null;
    function shouldAttemptMonitorDiscovery(force, discoveryDone, lastAttempt, now) {
        if (force || !discoveryDone) return true;
        const elapsed = Number(now ?? Date.now()) - Number(lastAttempt || 0);
        return elapsed >= DISCOVERY_RETRY_DELAY_MS;
    }

    async function ensureMonitorAddress(auto) {
        const shouldDiscover = shouldAttemptMonitorDiscovery(
            auto,
            GM_getValue(DISCOVERY_DONE_KEY, false),
            GM_getValue(DISCOVERY_LAST_ATTEMPT_KEY, 0),
            Date.now());
        if (!shouldDiscover) return true;

        if (monitorDiscoveryPromise) return await monitorDiscoveryPromise;

        const now = Date.now();
        const existingLock = GM_getValue(DISCOVERY_LOCK_KEY, null);
        if (existingLock && Number(existingLock.until || 0) > now) return false;
        const lockToken = `${now}-${Math.random().toString(36).slice(2)}`;
        GM_setValue(DISCOVERY_LOCK_KEY, { token: lockToken, until: now + DISCOVERY_LOCK_MS });
        monitorDiscoveryPromise = findMonitorAddress(false)
            .finally(() => {
                monitorDiscoveryPromise = null;
                const currentLock = GM_getValue(DISCOVERY_LOCK_KEY, null);
                if (currentLock?.token === lockToken)
                    GM_setValue(DISCOVERY_LOCK_KEY, null);
            });
        const found = await monitorDiscoveryPromise;
        if (found) {
            showNotification(`已连接上位机：${found}`);
            return true;
        }
        return false;
    }

    applyInstalledMonitorAddresses();

    // 专用工作页不显示业务菜单，避免用户误在工作页执行普通推送。
    if (!IS_REFUND_WORKER) {
        GM_registerMenuCommand('查看当前上位机', () => {
            showNotification(`已配对 ${getPairedMonitorAddresses().length} 台，当前：${getMonitorAddressText()}`);
        });
        GM_registerMenuCommand('切换上位机', () => {
            const addresses = getPairedMonitorAddresses();
            const current = getMonitorAddressText();
            const input = prompt(`请选择上位机编号：\n${addresses.map((address, index) => `${index + 1}. ${address}${address === current ? '（当前）' : ''}`).join('\n')}`, '1');
            if (input === null) return;
            const index = Number(input) - 1;
            if (!Number.isInteger(index) || index < 0 || index >= addresses.length) {
                showNotification('上位机编号无效');
                return;
            }
            const selected = normalizeAddress(addresses[index], DEFAULT_PORT);
            saveMonitorAddress(selected.host, selected.port);
            showNotification(`已切换上位机：${addresses[index]}`);
        });
        GM_registerMenuCommand('添加上位机', () => {
            const input = prompt('请输入新增监控端地址（局域网 IP:端口）：', '192.168.0.2:5280');
            if (!input) return;
            const value = String(input).trim().replace(/^https?:\/\//i, '').split('/')[0];
            const host = value.split(':')[0];
            if (!isAllowedMonitorHost(host)) {
                showNotification('地址无效，只支持局域网 IPv4 或 localhost');
                return;
            }
            const address = formatAddress(normalizeAddress(value, DEFAULT_PORT));
            const paired = getPairedMonitorAddresses();
            if (!paired.includes(address) && paired.length >= MAX_MONITOR_ADDRESSES) {
                showNotification(`最多配对 ${MAX_MONITOR_ADDRESSES} 台上位机`);
                return;
            }
            const addresses = normalizeAddressList([...paired, address]);
            const guideUrl = `${getBaseUrl(getMonitorAddressText())}/kuaidizs-install-guide?connect=${encodeURIComponent(addresses.join(','))}`;
            GM_openInTab(guideUrl, { active: true, setParent: true });
            showNotification('请在打开的安装页覆盖更新脚本，完成新增上位机授权');
        });
        GM_registerMenuCommand('移除上位机', () => {
            const addresses = getPairedMonitorAddresses();
            if (addresses.length <= 1) {
                showNotification('至少需要保留一台上位机');
                return;
            }
            const input = prompt(`请输入要移除的上位机编号：\n${addresses.map((address, index) => `${index + 1}. ${address}`).join('\n')}`);
            if (input === null) return;
            const index = Number(input) - 1;
            if (!Number.isInteger(index) || index < 0 || index >= addresses.length) {
                showNotification('上位机编号无效');
                return;
            }
            const remaining = addresses.filter((_, addressIndex) => addressIndex !== index);
            const active = remaining.includes(getMonitorAddressText()) ? getMonitorAddressText() : remaining[0];
            const selected = normalizeAddress(active, DEFAULT_PORT);
            saveMonitorAddress(selected.host, selected.port);
            GM_setValue(MONITOR_ADDRESSES_KEY, remaining);
            const guideUrl = `${getBaseUrl(active)}/kuaidizs-install-guide?connect=${encodeURIComponent(remaining.join(','))}`;
            GM_openInTab(guideUrl, { active: true, setParent: true });
            showNotification('请在打开的安装页覆盖更新脚本，完成移除');
        });
        GM_registerMenuCommand('重新连接上位机', async () => {
            showNotification('正在连接已配置的上位机...');
            const found = await findMonitorAddress(true);
            showNotification(found ? `已连接：${found}` : '连接失败，请从监控端安装页更新脚本或手动设置地址');
        });
        GM_registerMenuCommand('发送测试订单', async () => {
            await sendTestOrder();
        });
        GM_registerMenuCommand('立即推送订单数据', () => {
            extractAndPush();
        });
        GM_registerMenuCommand('重新打开退款核验工作页', () => {
            GM_setValue(REFUND_WORKER_HEARTBEAT_KEY, 0);
            ensureRefundWorker(true, true);
        });
    }

    // ============ 数据提取 ============
    function isTrueAttribute(element, name) {
        return String(element?.getAttribute(name) || '').toLowerCase() === 'true';
    }

    function extractRefundInfo(row) {
        const packageCheck = row.querySelector('.packageCheck');
        const refundStatuses = [];
        const refundProducts = [];

        row.querySelectorAll('.order_td').forEach(td => {
            const orderInput = td.querySelector('.orderInput');
            const refundStatus = (td.getAttribute('data-refund-status') ||
                orderInput?.getAttribute('data-refund-status') || '').trim().toUpperCase();
            if (!refundStatus || refundStatus === 'NO_REFUND') return;

            if (!refundStatuses.includes(refundStatus)) refundStatuses.push(refundStatus);
            const titleEl = td.querySelector('.packageOrder_title') || td.querySelector('.packageOrder_titleShort');
            const title = titleEl ? titleEl.textContent.trim() : '';
            if (title && !refundProducts.includes(title)) refundProducts.push(title);
        });

        return {
            hasRefund: isTrueAttribute(row, 'data-has-refund') ||
                isTrueAttribute(packageCheck, 'data-has-refund') || refundStatuses.length > 0,
            isPrintedRefund: isTrueAttribute(packageCheck, 'data-printed-refund'),
            refundStatus: refundStatuses.join(','),
            refundProductInfo: refundProducts.join('，')
        };
    }

    function extractTrackingNumber(row) {
        const sendSuccessTag = row.querySelector('.sendSuccessTag');
        if (sendSuccessTag) {
            const value = (sendSuccessTag.getAttribute('data-ydno') || sendSuccessTag.textContent || '').trim();
            if (value) return value;
        }

        const kdInput = row.querySelector('.kdNoInput');
        return kdInput ? (kdInput.value || kdInput.getAttribute('title') || '').trim() : '';
    }

    function extractOrders(options) {
        options = options || {};
        const orders = [];
        // 每个 .packageItem 是一个订单行
        document.querySelectorAll('tr.packageItem').forEach(row => {
            try {
                const togetherId = row.getAttribute('data-together-id') || '';

                // 1. 快递单号：从发货成功标签或快递单号输入框中提取
                const trackingNumber = extractTrackingNumber(row);

                // 2. 买家留言和卖家备注：从 checkbox 的 data 属性提取
                let buyerMessage = '';
                let sellerMemo = '';
                const checkbox = row.querySelector('.packageCheck');
                if (checkbox) {
                    buyerMessage = (checkbox.getAttribute('data-buyer-message') || '').trim();
                    sellerMemo = (checkbox.getAttribute('data-seller-memo') || '').trim();
                }

                // 也从备注容器的 JSON 中尝试提取
                const memoContainer = row.querySelector('.alternateMessageMemo');
                if (memoContainer) {
                    try {
                        const infoStr = memoContainer.getAttribute('data-info');
                        if (infoStr) {
                            const infoArr = JSON.parse(infoStr);
                            if (infoArr && infoArr.length > 0) {
                                if (!buyerMessage && infoArr[0].buyerMessage) buyerMessage = infoArr[0].buyerMessage.trim();
                                if (!sellerMemo && infoArr[0].customMemo) sellerMemo = infoArr[0].customMemo.trim();
                            }
                        }
                    } catch (e) { /* ignore parse error */ }
                }

                // 也从备注区块的可见文本提取
                if (!buyerMessage || !sellerMemo) {
                    const flagItems = row.querySelectorAll('.flagMeoItem');
                    flagItems.forEach(item => {
                        const spans = item.querySelectorAll('span');
                        spans.forEach(span => {
                            const text = span.textContent.trim();
                            const cls = span.className || '';
                            if (cls.includes('buyerMsg') || cls.includes('buyer_message')) {
                                if (!buyerMessage) buyerMessage = text;
                            }
                            if (cls.includes('sellerMemo') || cls.includes('seller_memo') || cls.includes('customMemo')) {
                                if (!sellerMemo) sellerMemo = text;
                            }
                        });
                    });
                }

                // 3. 商品信息：提取商品标题和数量
                const products = [];
                row.querySelectorAll('.order_td').forEach(td => {
                    const titleEl = td.querySelector('.packageOrder_title') || td.querySelector('.packageOrder_titleShort');
                    const title = titleEl ? titleEl.textContent.trim() : '';
                    const numEl = td.querySelector('dd span[style*="font-size: 14px"]');
                    const num = numEl ? numEl.textContent.trim() : '1';
                    if (title) {
                        products.push(num !== '1' ? `${title}×${num}` : title);
                    }
                });
                const productInfo = products.join('，');

                // 4. 订单号（淘宝交易号）
                const orderId = togetherId;
                const refundInfo = extractRefundInfo(row);

                if (trackingNumber || buyerMessage || sellerMemo || productInfo) {
                    orders.push({
                        trackingNumber: trackingNumber,
                        orderId: orderId,
                        buyerMessage: buyerMessage,
                        sellerMemo: sellerMemo,
                        productInfo: productInfo,
                        hasRefund: refundInfo.hasRefund,
                        isPrintedRefund: refundInfo.isPrintedRefund,
                        refundStatus: refundInfo.refundStatus,
                        refundProductInfo: refundInfo.refundProductInfo
                    });
                }
            } catch (e) {
                console.warn('[打包监控] 提取订单异常:', e);
            }
        });
        return orders;
    }

    // ============ 推送到上位机 ============
    function pushOrdersToAddress(addressText, orders) {
        const address = normalizeAddress(addressText, DEFAULT_PORT);
        return new Promise(resolve => {
            GM_xmlhttpRequest({
                method: 'POST',
                url: `${getBaseUrl(address.host, address.port)}/api/orderinfo`,
                headers: { 'Content-Type': 'application/json' },
                data: JSON.stringify(orders),
                timeout: 5000,
                onload: res => resolve({
                    address: formatAddress(address),
                    ok: res.status === 200,
                    status: res.status,
                    response: parseJsonResponse(res.responseText)
                }),
                onerror: () => resolve({ address: formatAddress(address), ok: false, error: 'connect' }),
                ontimeout: () => resolve({ address: formatAddress(address), ok: false, error: 'timeout' })
            });
        });
    }

    async function pushToMonitor(orders, options) {
        options = options || {};
        if (!orders || orders.length === 0) return { ok: false, confirmed: false, error: 'empty' };
        const addresses = getPairedMonitorAddresses();
        const results = await Promise.all(addresses.map(address => pushOrdersToAddress(address, orders)));
        const successful = results.filter(result => result.ok);
        const confirmed = !options.isTest || successful.some(result => Number(result.response?.testCount || 0) > 0);
        debugLog(`[打包监控] 订单广播完成: ${successful.length}/${addresses.length} 台`, results);

        if (!options.silent) {
            if (successful.length === 0) {
                showNotification(options.isTest ? '测试发送失败，请检查接收设备地址' : '订单发送失败，请检查接收设备网络');
            } else if (options.isTest) {
                showNotification(confirmed
                    ? `已有 ${successful.length}/${addresses.length} 台设备收到测试订单`
                    : `测试订单已发送至 ${successful.length}/${addresses.length} 台设备`);
            } else {
                showNotification(`已向 ${successful.length}/${addresses.length} 台设备推送 ${orders.length} 条订单`);
            }
        }

        return {
            ok: successful.length > 0,
            confirmed,
            successfulCount: successful.length,
            targetCount: addresses.length,
            results
        };
    }

    function requestMonitor(method, url, data, timeout) {
        return new Promise(resolve => {
            GM_xmlhttpRequest({
                method: method,
                url: url,
                headers: data ? { 'Content-Type': 'application/json' } : undefined,
                data: data ? JSON.stringify(data) : undefined,
                timeout: timeout || 3000,
                onload: res => resolve({ status: res.status, body: parseJsonResponse(res.responseText) }),
                onerror: () => resolve({ status: 0, body: {} }),
                ontimeout: () => resolve({ status: 0, body: {} })
            });
        });
    }

    function getConnectionClientId() {
        let clientId = String(GM_getValue(CONNECTION_CLIENT_ID_KEY, '') || '').trim();
        if (/^[A-Za-z0-9._:-]{8,128}$/.test(clientId)) return clientId;
        clientId = `userscript-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
        GM_setValue(CONNECTION_CLIENT_ID_KEY, clientId);
        return clientId;
    }

    async function sendConnectionHeartbeat() {
        const response = await requestMonitor('POST', getConnectionHeartbeatUrl(), {
            clientId: getConnectionClientId(),
            clientType: 'userscript',
            displayName: '快递端油猴脚本'
        }, 3000);
        if (response.status === 200) return true;
        await ensureMonitorAddress(false);
        return false;
    }

    async function startConnectionHeartbeat() {
        if (await ensureMonitorAddress(true))
            await sendConnectionHeartbeat();
        setInterval(sendConnectionHeartbeat, CONNECTION_HEARTBEAT_INTERVAL_MS);
    }

    function delay(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    function getOrderListSignature() {
        return Array.from(document.querySelectorAll('tr.packageItem')).map(row => {
            const togetherId = row.getAttribute('data-together-id') || '';
            const trackingNumber = extractTrackingNumber(row);
            const refundStatus = Array.from(row.querySelectorAll('[data-refund-status]'))
                .map(element => element.getAttribute('data-refund-status') || '')
                .join(',');
            return `${togetherId}|${trackingNumber}|${refundStatus}`;
        }).join('||');
    }

    let lastPrintedRefundFilterClickAt = 0;
    async function queryPrintedRefundSnapshot() {
        const selector = '[data-act-name="searchQuickQuery"][data-status="4"]';
        let refundFilter = document.querySelector(selector);
        if (!refundFilter) {
            throw new Error('当前不是快递助手批量打印页面，无法找到“打印后退款”快捷查询');
        }

        if (refundFilter.classList.contains('checked')) {
            return extractOrders();
        }

        const now = Date.now();
        if (!IS_REFUND_WORKER && isUserActivelyUsingPage(now)) {
            throw new Error('检测到用户正在操作快递助手，本次不切换页面，已使用监控端最近缓存');
        }
        if (now - lastPrintedRefundFilterClickAt < PRINTED_REFUND_FILTER_COOLDOWN_MS) {
            throw new Error('打印后退款查询正在安全冷却，已使用监控端最近缓存');
        }

        let lastSignature = getOrderListSignature();
        let listChanged = false;
        let stableSince = Date.now();
        lastPrintedRefundFilterClickAt = now;
        refundFilter.click();

        const deadline = Date.now() + PRINTED_REFUND_QUERY_TIMEOUT_MS;
        while (Date.now() < deadline) {
            await delay(100);
            const signature = getOrderListSignature();
            if (signature !== lastSignature) {
                lastSignature = signature;
                listChanged = true;
                stableSince = Date.now();
            }

            refundFilter = document.querySelector(selector);
            if (refundFilter?.classList.contains('checked') &&
                listChanged &&
                Date.now() - stableSince >= PRINTED_REFUND_STABLE_MS) {
                return extractOrders();
            }
        }

        throw new Error('打印后退款列表未在限定时间内完成刷新，已改用监控端最近缓存');
    }

    async function queryOrdersByTrackingNumbers(trackingNumbers) {
        const values = Array.from(new Set((trackingNumbers || []).map(value => String(value || '').trim().toUpperCase()).filter(Boolean)));
        if (values.length === 0) return [];

        const searchType = document.querySelector('select.extendSearchList');
        const searchTypeItem = document.querySelector('.extendSearchListLi li[data-value="sid"]');
        if (!searchType || !searchTypeItem) throw new Error('无法选择快递单号查询条件');

        searchType.value = 'sid';
        searchType.dispatchEvent(new Event('change', { bubbles: true }));
        searchTypeItem.click();
        await delay(150);

        const input = document.querySelector('input.extendSearchSidInput');
        const searchButton = document.querySelector('#printBatchSearchBtn[data-act-name="executeSearch"]');
        if (!input || !searchButton) throw new Error('无法找到快递单号查询输入框或查询按钮');

        input.value = values.join(',');
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));

        let signature = getOrderListSignature();
        let changed = false;
        let stableSince = Date.now();
        searchButton.click();
        const deadline = Date.now() + PRINTED_REFUND_QUERY_TIMEOUT_MS;
        while (Date.now() < deadline) {
            await delay(100);
            const nextSignature = getOrderListSignature();
            if (nextSignature !== signature) {
                signature = nextSignature;
                changed = true;
                stableSince = Date.now();
            }
            if (changed && Date.now() - stableSince >= PRINTED_REFUND_STABLE_MS)
                return extractOrders();
        }
        throw new Error('按快递单号查询超时');
    }

    async function queryRequestedRefundSnapshot(trackingNumbers) {
        const snapshot = await queryPrintedRefundSnapshot();
        const requested = Array.from(new Set((trackingNumbers || []).map(value => String(value || '').trim().toUpperCase()).filter(Boolean)));
        if (requested.length === 0) return snapshot;

        const found = new Set(snapshot.map(order => String(order.trackingNumber || '').trim().toUpperCase()).filter(Boolean));
        const missing = requested.filter(value => !found.has(value));
        if (missing.length === 0) return snapshot;

        try {
            const exactOrders = await queryOrdersByTrackingNumbers(missing);
            const merged = new Map();
            exactOrders.forEach(order => merged.set(String(order.trackingNumber || '').trim().toUpperCase(), order));
            snapshot.forEach(order => {
                const key = String(order.trackingNumber || '').trim().toUpperCase();
                if (!merged.has(key)) merged.set(key, order);
            });
            return Array.from(merged.values()).slice(0, 200);
        } catch (error) {
            console.warn('[打包监控] 历史订单精确查询失败，保留退款页快照:', error);
            return snapshot;
        }
    }

    let orderLookupPollStarted = false;
    async function pollOrderLookupRequests() {
        let reconnectDelay = ORDER_LOOKUP_RECONNECT_MS;
        try {
            if (!document.querySelector('tr.packageItem, select.extendSearchList')) {
                return;
            }
            const response = await requestMonitor('GET', getOrderLookupPendingUrl(), null, 25000);
            writeRefundWorkerHeartbeat();
            if (response.status === 200) reconnectDelay = 0;
            const pending = response.status === 200 ? response.body : null;
            if (pending?.pending && pending.requestId) {
                let orders = [];
                let success = false;
                let error = '';
                try {
                    orders = await queryRequestedRefundSnapshot(pending.trackingNumbers || []);
                    success = true;
                } catch (e) {
                    error = e?.message || String(e);
                    console.warn('[打包监控] 获取打印后退款列表失败:', error);
                }

                await requestMonitor('POST', getOrderLookupResultUrl(), {
                    requestId: pending.requestId,
                    success: success,
                    orders: orders,
                    error: error
                }, 5000);
                debugLog(`[打包监控] 已回传打印后退款订单快照: success=${success}, ${orders.length} 条`);
            }
        } catch (e) {
            debugLog('[打包监控] 轮询扫码核验请求失败:', e);
        } finally {
            if (reconnectDelay > 0) {
                setTimeout(pollOrderLookupRequests, reconnectDelay);
            } else {
                Promise.resolve().then(pollOrderLookupRequests);
            }
        }
    }

    function startOrderLookupPolling() {
        if (!IS_REFUND_WORKER) return;
        if (orderLookupPollStarted) return;
        orderLookupPollStarted = true;
        pollOrderLookupRequests();
    }

    function buildTestOrder() {
        const now = new Date();
        const hhmmss = [now.getHours(), now.getMinutes(), now.getSeconds()]
            .map(v => String(v).padStart(2, '0'))
            .join('');
        return [{
            trackingNumber: `TEST${hhmmss}`,
            orderId: '测试订单',
            buyerMessage: '这是一条测试买家留言',
            sellerMemo: '这是一条测试卖家备注',
            productInfo: '测试商品',
            isTest: true
        }];
    }

    let testOrderSending = false;
    async function sendTestOrder() {
        if (testOrderSending) {
            showNotification('测试订单正在发送，请稍候');
            return;
        }

        testOrderSending = true;
        showNotification(`正在向 ${getMonitorAddressText()} 发送测试订单`);
        try {
            await pushToMonitor(buildTestOrder(), { isTest: true, skipAddressDiscovery: true });
        } finally {
            testOrderSending = false;
        }
    }

    async function extractAndPush(options) {
        options = options || {};
        const orders = extractOrders(options);
        if (orders.length === 0) {
            if (!options.silent) showNotification('当前页面没有找到订单信息');
            return;
        }
        await pushToMonitor(orders, options);
    }

    // ============ 页面通知 ============
    function showNotification(msg) {
        const el = document.createElement('div');
        el.textContent = msg;
        Object.assign(el.style, {
            position: 'fixed', top: '20px', right: '20px', zIndex: '99999',
            padding: '12px 20px', borderRadius: '8px',
            background: 'rgba(0,0,0,.8)', color: '#fff', fontSize: '14px',
            fontFamily: 'Microsoft YaHei, sans-serif',
            boxShadow: '0 4px 12px rgba(0,0,0,.3)',
            transition: 'opacity .3s'
        });
        document.body.appendChild(el);
        setTimeout(() => { el.style.opacity = '0'; setTimeout(() => el.remove(), 300); }, 3000);
    }

    // ============ 自动推送：监听页面变化 + 操作按钮点击 ============
    let pushTimer = null;
    function schedulePush(options) {
        options = options || {};
        if (pushTimer) clearTimeout(pushTimer);
        pushTimer = setTimeout(() => {
            extractAndPush(options);
        }, 2000); // 页面加载/翻页后 2 秒自动推送
    }

    // 监听底部操作按钮点击（打印快递单、打印发货单、打印拣货单、发货等）
    function bindActionButtons() {
        const btnContainer = document.querySelector('.packageActionBtn') || document.querySelector('.packageActions');
        if (!btnContainer) return;
        btnContainer.addEventListener('click', (e) => {
            const btn = e.target.closest('input[type="button"], button, .btn_bluebig, .btn_pinkbig, .btn_cyanbig, .btn_graybig');
            if (btn) {
                debugLog('[打包监控] 检测到操作按钮点击:', btn.value || btn.textContent?.trim());
                schedulePush();
            }
        });
        debugLog('[打包监控] 操作按钮监听已绑定');
    }

    // 监听订单列表区域的 DOM 变化（放宽范围：监听整个容器或 body）
    const observer = new MutationObserver((mutations) => {
        for (const m of mutations) {
            if (m.addedNodes.length > 0) {
                for (const node of m.addedNodes) {
                    if (node.nodeType === 1) {
                        // 新增节点本身是订单行，或内部包含订单行，或是表格容器
                        if (node.classList?.contains('packageItem') ||
                            node.querySelector?.('.packageItem') ||
                            node.tagName === 'TBODY' ||
                            node.tagName === 'TABLE' ||
                            node.classList?.contains('dfdd_container')) {
                            debugLog('[打包监控] 检测到订单DOM变化，准备推送');
                            schedulePush();
                            return;
                        }
                    }
                }
            }
            // 也监听子节点批量替换（如翻页时 innerHTML 整体替换）
            if (m.removedNodes.length > 0 && m.addedNodes.length > 0) {
                debugLog('[打包监控] 检测到DOM子节点替换，准备推送');
                schedulePush();
                return;
            }
        }
    });

    startConnectionHeartbeat();

    if (IS_REFUND_WORKER) {
        setTimeout(async () => {
            if (!await startRefundWorkerHeartbeat()) return;
            await ensureMonitorAddress(true);
            startOrderLookupPolling();
        }, 0);
    } else {
        // 普通页面只负责订单推送，不再领取退款请求或切换筛选。
        setTimeout(async () => {
            await saveRefundWorkerTabIdentity(false);
            const target = document.querySelector('.dfdd_container') ||
                           document.querySelector('.packageItem')?.closest('table')?.parentElement ||
                           document.body;
            observer.observe(target, { childList: true, subtree: true });
            debugLog('[打包监控] DOM 监听已启动, 目标:', target.tagName, target.className || '(body)');
            bindActionButtons();
            const monitorReachable = await ensureMonitorAddress(true);
            extractAndPush();
            maintainRefundWorker(monitorReachable);
            setInterval(() => maintainRefundWorker(), REFUND_WORKER_RECHECK_INTERVAL_MS);
        }, 3000);
    }

    debugLog('[打包监控] 油猴脚本已加载', CHANGELOG);
})();
