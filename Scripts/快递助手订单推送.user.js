// ==UserScript==
// @name         订单备注播报插件
// @namespace    https://github.com/ExpressPackingMonitoring
// @version      1.3
// @description  从快递助手批量打印页面提取买家留言和卖家备注，发送到监控工位，打包时自动播报。v1.3 合并地址端口设置并统一回环地址。
// @author       ExpressPackingMonitoring
// @match        *://p4.kuaidizs.cn/*
// @match        *://kuaidizs.cn/*
// @match        *://*.kuaidizs.cn/*
// @connect      localhost
// @connect      127.0.0.1
// @connect      *
// @grant        GM_xmlhttpRequest
// @grant        GM_registerMenuCommand
// @grant        GM_getValue
// @grant        GM_setValue
// @run-at       document-idle
// ==/UserScript==

(function () {
    'use strict';

    // ============ 配置 ============
    const DEFAULT_HOST = '127.0.0.1';
    const DEFAULT_PORT = 5280;
    const DEFAULT_ADDRESS = `${DEFAULT_HOST}:${DEFAULT_PORT}`;
    const DISCOVERY_DONE_KEY = 'monitor_auto_discovery_done';
    const DISCOVERY_TIMEOUT = 700;
    const DISCOVERY_BATCH_SIZE = 32;
    const CHANGELOG = 'v1.3：合并上位机地址和端口设置，统一 127.x 回环地址为 127.0.0.1';

    function getApiUrl() { return `${getBaseUrl(getMonitorAddressText())}/api/orderinfo`; }
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
        const parsedPort = Number(parts[1] || port || DEFAULT_PORT);
        return {
            host: normalizedHost,
            port: Number.isInteger(parsedPort) && parsedPort > 0 && parsedPort <= 65535 ? parsedPort : DEFAULT_PORT
        };
    }
    function formatAddress(address) {
        const normalized = normalizeAddress(address.host, address.port);
        return `${normalized.host}:${normalized.port}`;
    }
    function getMonitorAddressText() {
        const storedAddress = GM_getValue('monitor_address', '');
        if (storedAddress) return formatAddress(normalizeAddress(storedAddress, DEFAULT_PORT));

        const legacyHost = GM_getValue('monitor_host', '');
        const legacyPort = GM_getValue('monitor_port', '');
        if (legacyHost || legacyPort) return formatAddress(normalizeAddress(legacyHost || DEFAULT_HOST, legacyPort || DEFAULT_PORT));

        return DEFAULT_ADDRESS;
    }
    function getMonitorAddress() {
        return normalizeAddress(getMonitorAddressText(), DEFAULT_PORT);
    }
    function saveMonitorAddress(host, port) {
        const address = normalizeAddress(host, port);
        GM_setValue('monitor_address', formatAddress(address));
        GM_setValue('monitor_host', address.host);
        GM_setValue('monitor_port', address.port);
        GM_setValue(DISCOVERY_DONE_KEY, true);
        return address;
    }

    function gmGet(url, timeout) {
        return new Promise(resolve => {
            GM_xmlhttpRequest({
                method: 'GET',
                url,
                timeout,
                onload: res => resolve(res.status >= 200 && res.status < 300),
                onerror: () => resolve(false),
                ontimeout: () => resolve(false)
            });
        });
    }

    async function canConnectMonitor(host, port) {
        return gmGet(getStorageUrl(host, port), DISCOVERY_TIMEOUT);
    }

    function getHostPrefix(host) {
        const match = String(host || '').match(/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.\d{1,3}$/);
        return match ? `${match[1]}.${match[2]}.${match[3]}` : '';
    }

    async function getWebRtcLocalPrefixes() {
        const PeerConnection = window.RTCPeerConnection || window.webkitRTCPeerConnection;
        if (!PeerConnection) return [];

        const prefixes = new Set();
        const ipPattern = /\b(?:10|172\.(?:1[6-9]|2\d|3[01])|192\.168)\.\d{1,3}\.\d{1,3}\b/g;
        const collect = text => {
            for (const ip of String(text || '').match(ipPattern) || []) {
                const prefix = getHostPrefix(ip);
                if (prefix) prefixes.add(prefix);
            }
        };

        return new Promise(resolve => {
            const pc = new PeerConnection({ iceServers: [] });
            const done = () => {
                try { pc.close(); } catch (e) { /* ignore */ }
                resolve(Array.from(prefixes));
            };
            pc.onicecandidate = event => {
                if (!event.candidate) {
                    done();
                    return;
                }
                collect(event.candidate.candidate);
            };
            try {
                pc.createDataChannel('monitor-discovery');
                pc.createOffer()
                    .then(offer => {
                        collect(offer.sdp);
                        return pc.setLocalDescription(offer);
                    })
                    .catch(done);
            } catch (e) {
                done();
                return;
            }
            setTimeout(done, 900);
        });
    }

    async function findMonitorAddress(showProgress) {
        const saved = getMonitorAddress();
        const port = saved.port;
        const directCandidates = [
            saved.host,
            '127.0.0.1',
            'localhost'
        ].filter((host, index, arr) => host && arr.indexOf(host) === index);

        for (const host of directCandidates) {
            if (showProgress) showNotification(`正在探测 ${host}:${port}`);
            if (await canConnectMonitor(host, port)) {
                const found = saveMonitorAddress(host, port);
                return `${found.host}:${found.port}`;
            }
        }

        const prefixes = new Set();
        const savedPrefix = getHostPrefix(saved.host);
        if (savedPrefix) prefixes.add(savedPrefix);
        for (const prefix of await getWebRtcLocalPrefixes()) prefixes.add(prefix);
        if (prefixes.size === 0) {
            ['192.168.0', '192.168.1', '192.168.31', '10.0.0'].forEach(prefix => prefixes.add(prefix));
        }

        for (const prefix of prefixes) {
            for (let start = 1; start <= 254; start += DISCOVERY_BATCH_SIZE) {
                if (showProgress) showNotification(`正在查找 ${prefix}.x`);
                const hosts = Array.from(
                    { length: Math.min(DISCOVERY_BATCH_SIZE, 255 - start) },
                    (_, index) => `${prefix}.${start + index}`
                );
                const checks = hosts.map(async host => ({ host, ok: await canConnectMonitor(host, port) }));
                const results = await Promise.all(checks);
                const found = results.find(item => item.ok);
                if (found) {
                    const address = saveMonitorAddress(found.host, port);
                    return `${address.host}:${address.port}`;
                }
            }
        }

        GM_setValue(DISCOVERY_DONE_KEY, true);
        return '';
    }

    async function ensureMonitorAddress(auto) {
        const shouldDiscover = auto || !GM_getValue(DISCOVERY_DONE_KEY, false);
        if (!shouldDiscover) return true;

        const found = await findMonitorAddress(false);
        if (found) {
            showNotification(`已自动填入上位机地址：${found}`);
            return true;
        }
        return false;
    }

    // 注册菜单命令用于修改配置
    GM_registerMenuCommand('⚙️ 设置上位机地址', () => {
        const input = prompt('请输入打包监控上位机地址（IP:端口 或 http://IP:端口）：', getMonitorAddressText());
        if (input) {
            const address = saveMonitorAddress(input.trim(), DEFAULT_PORT);
            showNotification(`已设置上位机地址：${formatAddress(address)}`);
        }
    });
    GM_registerMenuCommand('自动探测上位机地址', async () => {
        showNotification('正在自动探测上位机地址...');
        const found = await findMonitorAddress(true);
        showNotification(found ? `已自动填入：${found}` : '未找到上位机，请确认已开启 Web 服务并在同一局域网');
    });
    GM_registerMenuCommand('发送测试订单', () => {
        sendTestOrder();
    });
    GM_registerMenuCommand('🔄 立即推送订单数据', () => {
        extractAndPush();
    });

    // ============ 数据提取 ============
    function extractOrders() {
        const orders = [];
        // 每个 .packageItem 是一个订单行
        document.querySelectorAll('tr.packageItem').forEach(row => {
            try {
                const togetherId = row.getAttribute('data-together-id') || '';

                // 1. 快递单号：从发货成功标签或快递单号输入框中提取
                let trackingNumber = '';
                const sendSuccessTag = row.querySelector('.sendSuccessTag');
                if (sendSuccessTag) {
                    trackingNumber = (sendSuccessTag.getAttribute('data-ydno') || sendSuccessTag.textContent || '').trim();
                }
                if (!trackingNumber) {
                    const kdInput = row.querySelector('.kdNoInput');
                    if (kdInput) trackingNumber = (kdInput.value || kdInput.getAttribute('title') || '').trim();
                }

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

                if (trackingNumber || buyerMessage || sellerMemo || productInfo) {
                    orders.push({
                        trackingNumber: trackingNumber,
                        orderId: orderId,
                        buyerMessage: buyerMessage,
                        sellerMemo: sellerMemo,
                        productInfo: productInfo
                    });
                }
            } catch (e) {
                console.warn('[打包监控] 提取订单异常:', e);
            }
        });
        return orders;
    }

    // ============ 推送到上位机 ============
    function parseJsonResponse(text) {
        try { return JSON.parse(text || '{}'); } catch (e) { return {}; }
    }

    async function pushToMonitor(orders, options) {
        options = options || {};
        if (!orders || orders.length === 0) return { ok: false, confirmed: false, error: 'empty' };
        await ensureMonitorAddress(false);

        return new Promise(resolve => {
            GM_xmlhttpRequest({
                method: 'POST',
                url: getApiUrl(),
                headers: { 'Content-Type': 'application/json' },
                data: JSON.stringify(orders),
                timeout: 5000,
                onload: function (res) {
                    const response = parseJsonResponse(res.responseText);
                    if (res.status === 200) {
                        console.log(`[打包监控] 推送成功: ${orders.length} 条`, response);
                        const confirmed = !options.isTest || Number(response.testCount || 0) > 0;
                        if (options.isTest) {
                            showNotification(confirmed ? '监控工位已收到测试订单' : '测试订单已发送，请查看监控端是否播报');
                        } else {
                            showNotification(`✅ 已推送 ${orders.length} 条订单到打包监控`);
                        }
                        resolve({ ok: true, confirmed, response });
                        return;
                    }

                    console.warn('[打包监控] 推送失败:', res.status, res.responseText);
                    showNotification(options.isTest ? '测试发送失败，请检查监控工位地址' : `❌ 推送失败: ${res.status}`);
                    resolve({ ok: false, confirmed: false, status: res.status, response });
                },
                onerror: function (err) {
                    console.warn('[打包监控] 连接失败，请确认上位机已启动 Web 服务:', err);
                    showNotification(options.isTest ? '测试发送失败，请检查监控工位地址' : '⚠️ 无法连接上位机，请检查 IP/端口设置');
                    resolve({ ok: false, confirmed: false, error: 'connect' });
                },
                ontimeout: function () {
                    console.warn('[打包监控] 推送超时');
                    showNotification(options.isTest ? '测试发送超时，请检查监控工位地址' : '⏱ 推送超时，请检查网络');
                    resolve({ ok: false, confirmed: false, error: 'timeout' });
                }
            });
        });
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

    async function sendTestOrder() {
        showNotification('正在发送测试订单，用于确认监控工位能否收到订单备注');
        await pushToMonitor(buildTestOrder(), { isTest: true });
    }

    async function extractAndPush() {
        const orders = extractOrders();
        if (orders.length === 0) {
            showNotification('📭 当前页面没有找到订单信息');
            return;
        }
        await pushToMonitor(orders);
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
    function schedulePush() {
        if (pushTimer) clearTimeout(pushTimer);
        pushTimer = setTimeout(() => {
            extractAndPush();
        }, 2000); // 页面加载/翻页后 2 秒自动推送
    }

    // 监听底部操作按钮点击（打印快递单、打印发货单、打印拣货单、发货等）
    function bindActionButtons() {
        const btnContainer = document.querySelector('.packageActionBtn') || document.querySelector('.packageActions');
        if (!btnContainer) return;
        btnContainer.addEventListener('click', (e) => {
            const btn = e.target.closest('input[type="button"], button, .btn_bluebig, .btn_pinkbig, .btn_cyanbig, .btn_graybig');
            if (btn) {
                console.log('[打包监控] 检测到操作按钮点击:', btn.value || btn.textContent?.trim());
                schedulePush();
            }
        });
        console.log('[打包监控] 操作按钮监听已绑定');
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
                            console.log('[打包监控] 检测到订单DOM变化，准备推送');
                            schedulePush();
                            return;
                        }
                    }
                }
            }
            // 也监听子节点批量替换（如翻页时 innerHTML 整体替换）
            if (m.removedNodes.length > 0 && m.addedNodes.length > 0) {
                console.log('[打包监控] 检测到DOM子节点替换，准备推送');
                schedulePush();
                return;
            }
        }
    });

    // 延迟启动观察，等页面加载
    setTimeout(() => {
        // 优先监听订单列表容器，fallback 到 body
        const target = document.querySelector('.dfdd_container') ||
                       document.querySelector('.packageItem')?.closest('table')?.parentElement ||
                       document.body;
        observer.observe(target, { childList: true, subtree: true });
        console.log('[打包监控] DOM 监听已启动, 目标:', target.tagName, target.className || '(body)');
        // 绑定操作按钮监听
        bindActionButtons();
        ensureMonitorAddress(true);
        // 首次推送
        extractAndPush();
    }, 3000);

    console.log('[打包监控] 油猴脚本已加载', CHANGELOG);
})();
