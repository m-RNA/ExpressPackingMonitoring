// ==UserScript==
// @name         快递助手 → 打包监控联动
// @namespace    https://github.com/ExpressPackingMonitoring
// @version      1.0
// @description  从快递助手批量打印页面提取订单信息（快递单号、买家留言、卖家备注、商品名），推送到打包监控上位机，扫码时自动语音播报。
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

    function getHost() { return GM_getValue('monitor_host', DEFAULT_HOST); }
    function getPort() { return GM_getValue('monitor_port', DEFAULT_PORT); }
    function getApiUrl() { return `http://${getHost()}:${getPort()}/api/orderinfo`; }

    // 注册菜单命令用于修改配置
    GM_registerMenuCommand('⚙️ 设置上位机地址', () => {
        const host = prompt('请输入打包监控上位机 IP 地址：', getHost());
        if (host) GM_setValue('monitor_host', host.trim());
    });
    GM_registerMenuCommand('⚙️ 设置上位机端口', () => {
        const port = prompt('请输入打包监控 Web 端口：', getPort());
        if (port && !isNaN(port)) GM_setValue('monitor_port', parseInt(port));
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
    function pushToMonitor(orders) {
        if (!orders || orders.length === 0) return;

        GM_xmlhttpRequest({
            method: 'POST',
            url: getApiUrl(),
            headers: { 'Content-Type': 'application/json' },
            data: JSON.stringify(orders),
            timeout: 5000,
            onload: function (res) {
                if (res.status === 200) {
                    console.log(`[打包监控] 推送成功: ${orders.length} 条`);
                    showNotification(`✅ 已推送 ${orders.length} 条订单到打包监控`);
                } else {
                    console.warn('[打包监控] 推送失败:', res.status, res.responseText);
                    showNotification(`❌ 推送失败: ${res.status}`);
                }
            },
            onerror: function (err) {
                console.warn('[打包监控] 连接失败，请确认上位机已启动 Web 服务:', err);
                showNotification('⚠️ 无法连接上位机，请检查 IP/端口设置');
            },
            ontimeout: function () {
                console.warn('[打包监控] 推送超时');
                showNotification('⏱ 推送超时，请检查网络');
            }
        });
    }

    function extractAndPush() {
        const orders = extractOrders();
        if (orders.length === 0) {
            showNotification('📭 当前页面没有找到订单信息');
            return;
        }
        pushToMonitor(orders);
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

    // ============ 自动推送：监听页面变化 ============
    let pushTimer = null;
    function schedulePush() {
        if (pushTimer) clearTimeout(pushTimer);
        pushTimer = setTimeout(() => {
            extractAndPush();
        }, 2000); // 页面加载/翻页后 2 秒自动推送
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
        // 首次推送
        extractAndPush();
    }, 3000);

    console.log('[打包监控] 油猴脚本已加载');
})();
