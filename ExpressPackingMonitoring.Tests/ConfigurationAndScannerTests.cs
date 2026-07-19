using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ConfigurationAndScannerTests
{
    [Fact]
    public void AppConfig_MaximizesSpeechVolumeByDefault()
    {
        Assert.True(new AppConfig().MaximizeVolumeForSpeech);
    }

    [Fact]
    public void AppConfig_LegacyJsonEnablesMaximumSpeechVolumeByDefault()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{}")!;

        Assert.True(config.MaximizeVolumeForSpeech);
    }

    [Fact]
    public void AppConfig_CameraIdleNoSleepPeriodsAreEmptyByDefault()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{}")!;

        Assert.Equal("", config.CameraIdleNoSleepStart1);
        Assert.Equal("", config.CameraIdleNoSleepEnd1);
        Assert.Equal("", config.CameraIdleNoSleepStart2);
        Assert.Equal("", config.CameraIdleNoSleepEnd2);
        Assert.False(config.IsCameraIdleNoSleepTime(new DateTime(2026, 7, 18, 14, 0, 0)));
    }

    [Theory]
    [InlineData(12, 44, false)]
    [InlineData(12, 45, true)]
    [InlineData(19, 29, true)]
    [InlineData(19, 30, false)]
    [InlineData(20, 30, true)]
    [InlineData(22, 44, true)]
    [InlineData(22, 45, false)]
    public void CameraIdleNoSleepPeriods_BlockOnlyInsideConfiguredRanges(int hour, int minute, bool expected)
    {
        var config = new AppConfig
        {
            CameraIdleNoSleepStart1 = "12:45",
            CameraIdleNoSleepEnd1 = "19:30",
            CameraIdleNoSleepStart2 = "20:30",
            CameraIdleNoSleepEnd2 = "22:45"
        };

        Assert.Equal(expected, config.IsCameraIdleNoSleepTime(new DateTime(2026, 7, 18, hour, minute, 0)));
    }

    [Theory]
    [InlineData(22, 59, true)]
    [InlineData(0, 30, true)]
    [InlineData(1, 0, false)]
    public void CameraIdleNoSleepPeriod_SupportsCrossingMidnight(int hour, int minute, bool expected)
    {
        var config = new AppConfig
        {
            CameraIdleNoSleepStart1 = "22:30",
            CameraIdleNoSleepEnd1 = "01:00"
        };

        Assert.Equal(expected, config.IsCameraIdleNoSleepTime(new DateTime(2026, 7, 18, hour, minute, 0)));
    }

    [Theory]
    [InlineData("", "", true, "", "")]
    [InlineData(" 8:05 ", " 9:30 ", true, "08:05", "09:30")]
    [InlineData("08:00", "", false, "08:00", "")]
    [InlineData("25:00", "26:00", false, "25:00", "26:00")]
    [InlineData("08:00", "08:00", false, "08:00", "08:00")]
    public void TryNormalizeCameraIdlePeriod_ValidatesAndNormalizesTimeRange(
        string start,
        string end,
        bool expected,
        string expectedStart,
        string expectedEnd)
    {
        bool actual = AppConfig.TryNormalizeCameraIdlePeriod(start, end, out string normalizedStart, out string normalizedEnd);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedStart, normalizedStart);
        Assert.Equal(expectedEnd, normalizedEnd);
    }

    [Fact]
    public void BuildPreviewOrderNotice_IncludesOrderDetailsAndRefundException()
    {
        var orderInfo = new OrderInfo
        {
            BuyerMessage = "放门口",
            SellerMemo = "检查颜色",
            ProductInfo = "蓝色外套",
            IsPrintedRefund = true,
            RefundStatus = "SUCCESS"
        };

        string notice = MainViewModel.BuildPreviewOrderNotice(orderInfo);

        Assert.Contains("放门口", notice);
        Assert.Contains("检查颜色", notice);
        Assert.Contains("蓝色外套", notice);
        Assert.Contains("退款已完成", notice);
        Assert.Equal(4, notice.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void BuildPreviewOrderNotice_SeparatesRemarksFromProductDetails()
    {
        var orderInfo = new OrderInfo
        {
            BuyerMessage = "放门口",
            SellerMemo = "检查颜色",
            ProductInfo = "蓝色外套"
        };

        string remarks = MainViewModel.BuildPreviewOrderRemarkNotice(orderInfo);
        string details = MainViewModel.BuildPreviewOrderDetailNotice(orderInfo);

        Assert.Contains("放门口", remarks);
        Assert.Contains("检查颜色", remarks);
        Assert.DoesNotContain("蓝色外套", remarks);
        Assert.Contains("蓝色外套", details);
    }

    [Theory]
    [InlineData("提示：开始录像", AlertPriority.Normal, AlertSound.None, false)]
    [InlineData("警告：重复单号", AlertPriority.Normal, AlertSound.None, true)]
    [InlineData("Invalid order number", AlertPriority.Normal, AlertSound.None, true)]
    [InlineData("需要人工核对", AlertPriority.Critical, AlertSound.None, true)]
    [InlineData("单号不一致", AlertPriority.Normal, AlertSound.Warning, true)]
    public void ShouldShowPreviewAlert_OnlyRoutesExceptionsToPreview(
        string message,
        AlertPriority priority,
        AlertSound sound,
        bool expected)
    {
        var request = new AlertRequest { Message = message, Priority = priority, Sound = sound };

        Assert.Equal(expected, MainViewModel.ShouldShowPreviewAlert(request));
    }

    [Theory]
    [InlineData(0x0112, 0xF060, "WindowSystemCommandClose")]
    [InlineData(0x0010, 0, "WindowCloseMessage")]
    [InlineData(0x0011, 0, "WindowsQueryEndSession")]
    [InlineData(0x0016, 1, "WindowsEndSession")]
    public void ClassifyShutdownWindowMessage_IdentifiesCloseSources(int message, long wParam, string expected)
    {
        Assert.Equal(expected, RuntimeLog.ClassifyShutdownWindowMessage(message, new IntPtr(wParam)));
    }

    [Fact]
    public void ClassifyShutdownWindowMessage_IgnoresCancelledEndSession()
    {
        Assert.Null(RuntimeLog.ClassifyShutdownWindowMessage(0x0016, IntPtr.Zero));
    }

    [Fact]
    public void FormatStartupArguments_LogsRolesAndRedactsUnknownValues()
    {
        string result = RuntimeLog.FormatStartupArguments(new[]
        {
            "--role", "camera", "private-value", "--custom=secret"
        });

        Assert.Equal("--role CameraMonitor <redacted> --custom", result);
    }

    [Fact]
    public void RefundWorkerUserscript_IsolatesLookupFromUserPage()
    {
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "快递助手订单推送.user.js");
        string script = File.ReadAllText(scriptPath);

        Assert.Contains("// @grant        GM_openInTab", script);
        Assert.Contains("// @grant        GM_getTabs", script);
        Assert.Contains("const IS_REFUND_WORKER", script);
        Assert.Contains("GM_openInTab(buildRefundWorkerUrl(), { active: false, setParent: false })", script);
        Assert.Contains("【退款核验专用】请勿操作", script);
        Assert.Contains("data-epm-refund-worker-overlay", script);
        Assert.Contains("请勿操作或关闭此页面", script);
        Assert.Contains("claimRefundWorkerLease()", script);
        Assert.Contains("ownedHeartbeat.token !== REFUND_WORKER_TOKEN", script);
        Assert.Contains("closeDuplicateRefundWorker()", script);
        Assert.Contains("hasOpenRefundWorkerTab()", script);
        Assert.Contains("saveRefundWorkerTabIdentity(true)", script);
        Assert.Contains("if (!force && monitorReachable !== true) return", script);
        Assert.Contains("maintainRefundWorker(monitorReachable)", script);
        Assert.Contains("REFUND_WORKER_HEARTBEAT_INTERVAL_MS = 30000", script);
        Assert.Contains("REFUND_WORKER_RECHECK_INTERVAL_MS = 30000", script);
        Assert.Contains("const REFUND_WORKER_STALE_MS = 10 * 60 * 1000", script);
        Assert.Contains("if (!event.persisted) releaseRefundWorkerLease()", script);
        Assert.Contains("if (!IS_REFUND_WORKER) return;", script);
        Assert.Contains("普通页面只负责订单推送，不再领取退款请求或切换筛选", script);
        Assert.Contains("writeRefundWorkerHeartbeat();", script);
        Assert.Contains("queryRequestedRefundSnapshot(pending.trackingNumbers || [])", script);
        Assert.Contains("input.extendSearchSidInput", script);
    }

    [Fact]
    public void Userscript_AvoidsSubnetScanningAndSendsTestOrderDirectly()
    {
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "快递助手订单推送.user.js");
        string script = File.ReadAllText(scriptPath);

        Assert.DoesNotContain("// @connect      *", script);
        Assert.DoesNotContain("RTCPeerConnection", script);
        Assert.DoesNotContain("start <= 254", script);
        Assert.Contains("applyInstalledMonitorAddress();", script);
        Assert.Contains("await pushToMonitor(buildTestOrder(), { isTest: true, skipAddressDiscovery: true });", script);
        Assert.DoesNotContain("const connected = await ensureMonitorAddress(true);", script);
    }

    [Fact]
    public void AddMonitorConnectPermission_AddsExactHostWithoutRequiringWildcardPermission()
    {
        const string script = "// ==UserScript==\n// @connect      localhost\n// ==/UserScript==\nconst INSTALL_MONITOR_ADDRESS = '';";

        string customized = PrintToolInstallGuide.AddMonitorConnectPermission(script, "192.168.2.239:5280");
        string repeated = PrintToolInstallGuide.AddMonitorConnectPermission(customized, "http://192.168.2.239:5280");

        Assert.Contains("// @connect      192.168.2.239", customized);
        Assert.DoesNotContain("// @connect      *", customized);
        Assert.Contains("const INSTALL_MONITOR_ADDRESS = '192.168.2.239:5280';", customized);
        Assert.Equal(customized, repeated);
    }

    [Fact]
    public void AddMonitorConnectPermission_PreservesCrLfLineEndings()
    {
        const string script = "// ==UserScript==\r\n// @connect      localhost\r\n// @connect      *\r\n// ==/UserScript==\r\n";

        string customized = PrintToolInstallGuide.AddMonitorConnectPermission(script, "192.168.2.239:5280");

        Assert.DoesNotContain("\n", customized.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildAccessSetupCommand_UsesLanguageIndependentUserSid()
    {
        string command = WebServer.BuildAccessSetupCommand(5280, "S-1-5-21-1000");

        Assert.Contains("url=http://+:5280/", command);
        Assert.Contains("sddl=\"D:(A;;GX;;;S-1-5-21-1000)\"", command);
        Assert.Contains("localport=5280", command);
        Assert.Contains("delete urlacl", command);
        Assert.Contains("firewall delete rule", command);
        Assert.DoesNotContain("user=Everyone", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAccessSetupCommand_FirewallRepairDoesNotRewriteUrlAcl()
    {
        string command = WebServer.BuildAccessSetupCommand(
            5280,
            "S-1-5-21-1000",
            includeUrlAcl: false);

        Assert.DoesNotContain("urlacl", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("firewall delete rule", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("firewall add rule", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localport=5280", command);
    }

    [Theory]
    [InlineData("5280", 5280, true)]
    [InlineData("80,443,5280", 5280, true)]
    [InlineData("5000-5300", 5280, true)]
    [InlineData("*", 5280, true)]
    [InlineData("80,443", 5280, false)]
    [InlineData("", 5280, false)]
    public void FirewallPortsContain_RecognizesSingleAndRangePorts(
        string configuredPorts,
        int expectedPort,
        bool expected)
    {
        Assert.Equal(expected, WebServer.FirewallPortsContain(configuredPorts, expectedPort));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ShouldRepairLanAccessAtStartup_RequiresCompletedSetupAndEnabledService(
        bool setupCompleted,
        bool webServerEnabled,
        bool expected)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = setupCompleted,
            EnableWebServer = webServerEnabled
        };

        Assert.Equal(expected, MainViewModel.ShouldRepairLanAccessAtStartup(config));
    }


    [Fact]
    public void NormalizeAfterLoad_ResolvesConflictingScannerModesAndBounds()
    {
        var config = new AppConfig
        {
            EnableGlobalKeyboard = true,
            EnableScannerAutoSubmit = true,
            ScannerAutoSubmitMinLength = 1,
            ScannerAutoSubmitQuietMs = 5000,
            ScannerAutoSubmitMaxAverageIntervalMs = 1,
            ScannerAutoSubmitMaxKeyIntervalMs = 1000
        };

        bool changed = AppConfig.NormalizeAfterLoad(config);

        Assert.True(changed);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.Equal(4, config.ScannerAutoSubmitMinLength);
        Assert.Equal(600, config.ScannerAutoSubmitQuietMs);
        Assert.Equal(10, config.ScannerAutoSubmitMaxAverageIntervalMs);
        Assert.Equal(150, config.ScannerAutoSubmitMaxKeyIntervalMs);
        Assert.Equal(32, config.WebAccessKey.Length);
    }

    [Theory]
    [InlineData(new double[] { 20, 25, 30, 20 }, 5, true)]
    [InlineData(new double[] { 20, 250, 20, 250 }, 5, false)]
    [InlineData(new double[] { 20 }, 3, false)]
    public void IsFastSequence_DistinguishesScannerAndManualTyping(
        double[] intervals,
        int characterCount,
        bool expected)
    {
        bool actual = ScannerAutoSubmitPolicy.IsFastSequence(
            intervals,
            characterCount,
            maxAverageIntervalMs: 60,
            maxKeyIntervalMs: 100);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("发货", true, true, true)]
    [InlineData("退货", true, true, true)]
    [InlineData("发货", false, true, false)]
    [InlineData("退货", false, true, false)]
    [InlineData("发货", true, false, false)]
    [InlineData("其他", true, true, false)]
    public void ShouldAlertPrintedRefund_AlertsEnabledShippingAndReturnScans(
        string mode,
        bool alertEnabled,
        bool isPrintedRefund,
        bool expected)
    {
        var orderInfo = new OrderInfo { IsPrintedRefund = isPrintedRefund };

        bool actual = MainViewModel.ShouldAlertPrintedRefund(mode, alertEnabled, orderInfo);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("NO_REFUND", false)]
    [InlineData("WAIT_SELLER_AGREE", true)]
    [InlineData("WAIT_BUYER_RETURN_GOODS", true)]
    [InlineData("WAIT_SELLER_CONFIRM_GOODS", true)]
    [InlineData("SUCCESS", true)]
    [InlineData("CLOSED", true)]
    public void ShouldAlertPrintedRefund_UsesRefundStatus(string refundStatus, bool expected)
    {
        var orderInfo = new OrderInfo
        {
            IsPrintedRefund = true,
            RefundStatus = refundStatus
        };

        Assert.Equal(expected, MainViewModel.ShouldAlertPrintedRefund("发货", true, orderInfo));
    }

    [Theory]
    [InlineData("NO_REFUND", "无退款")]
    [InlineData("WAIT_SELLER_AGREE", "等待卖家处理退款")]
    [InlineData("WAIT_BUYER_RETURN_GOODS", "等待买家退货")]
    [InlineData("WAIT_SELLER_CONFIRM_GOODS", "等待卖家确认收到退货")]
    [InlineData("SUCCESS", "退款已完成")]
    [InlineData("CLOSED", "退款流程已关闭或取消")]
    public void GetRefundStatusDisplayText_MapsKnownStatuses(string refundStatus, string expected)
    {
        var orderInfo = new OrderInfo { RefundStatus = refundStatus };

        Assert.Equal(expected, MainViewModel.GetRefundStatusDisplayText(orderInfo));
    }

    [Fact]
    public void GetPrintedRefundLookupDelay_RequestsImmediatelyThenThrottlesForFiveSeconds()
    {
        DateTime now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.Zero, MainViewModel.GetPrintedRefundLookupDelay(DateTime.MinValue, now));
        Assert.Equal(TimeSpan.FromSeconds(3), MainViewModel.GetPrintedRefundLookupDelay(now.AddSeconds(-2), now));
        Assert.Equal(TimeSpan.Zero, MainViewModel.GetPrintedRefundLookupDelay(now.AddSeconds(-5), now));
    }

    [Fact]
    public void BuildOrderInfoSpeechFollowups_PreservesBuyerAndSellerRemarksAfterRefundAlert()
    {
        var orderInfo = new OrderInfo
        {
            BuyerMessage = "放门口",
            SellerMemo = "检查颜色",
            ProductInfo = "蓝色外套",
            IsPrintedRefund = true,
            RefundStatus = "SUCCESS"
        };

        IReadOnlyList<AlertSpeechFollowup> followups = MainViewModel.BuildOrderInfoSpeechFollowups(
            orderInfo,
            announcementsEnabled: true,
            announceBuyerMessage: true,
            announceSellerMemo: true,
            announceProductInfo: false);

        Assert.Collection(
            followups,
            buyer =>
            {
                Assert.Contains("放门口", buyer.Text);
                Assert.Equal(AlertSound.Remark, buyer.Sound);
            },
            seller =>
            {
                Assert.Contains("检查颜色", seller.Text);
                Assert.Equal(AlertSound.Remark, seller.Sound);
            });
    }

    [Fact]
    public void AlertService_CriticalAlertBlocksNormalAlertUntilDisplayEnds()
    {
        DateTime now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var presented = new List<string>();
        var played = new List<string>();
        using var alerts = new AlertService(
            request => presented.Add(request.Message),
            request => played.Add(request.SpeechText),
            () => now);

        AlertPublishResult criticalResult = alerts.Publish(new AlertRequest
        {
            Message = "退款警告",
            SpeechText = "退款语音",
            Priority = AlertPriority.Critical,
            DisplayDuration = TimeSpan.FromSeconds(12)
        });
        AlertPublishResult blockedResult = alerts.Publish(new AlertRequest
        {
            Message = "重复单号",
            SpeechText = "重复单号",
            Priority = AlertPriority.Normal
        });

        now = now.AddSeconds(12);
        AlertPublishResult acceptedAfterExpiry = alerts.Publish(new AlertRequest
        {
            Message = "没有单号",
            SpeechText = "没有单号",
            Priority = AlertPriority.Normal
        });

        Assert.Equal(AlertPublishResult.Accepted, criticalResult);
        Assert.Equal(AlertPublishResult.DroppedByHigherPriority, blockedResult);
        Assert.Equal(AlertPublishResult.Accepted, acceptedAfterExpiry);
        Assert.Equal(new[] { "退款警告", "没有单号" }, presented);
        Assert.Equal(new[] { "退款语音", "没有单号" }, played);
    }

    [Fact]
    public void AlertService_DeduplicatesWithinConfiguredWindow()
    {
        DateTime now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        int presentationCount = 0;
        using var alerts = new AlertService(_ => presentationCount++, _ => { }, () => now);
        var request = new AlertRequest
        {
            Message = "重复单号",
            SpeechText = "重复单号",
            DeduplicationKey = "duplicate:123",
            DeduplicationWindow = TimeSpan.FromSeconds(3)
        };

        Assert.Equal(AlertPublishResult.Accepted, alerts.Publish(request));
        Assert.Equal(AlertPublishResult.DroppedAsDuplicate, alerts.Publish(request));
        now = now.AddSeconds(3);
        Assert.Equal(AlertPublishResult.Accepted, alerts.Publish(request));
        Assert.Equal(2, presentationCount);
    }

    [Fact]
    public void AlertService_ForwardsIndustrialAlarmOnceAndRefundSpeechThreeTimes()
    {
        AlertRequest? playedRequest = null;
        using var alerts = new AlertService(_ => { }, request => playedRequest = request);
        var refundAlert = new AlertRequest
        {
            Message = "退款警告",
            SpeechText = "订单有退款，请核对",
            Priority = AlertPriority.Critical,
            Sound = AlertSound.IndustrialAlarm,
            SoundRepeatCount = 1,
            SpeechRepeatCount = 3,
            FollowupSpeech =
            [
                new AlertSpeechFollowup { Text = "买家留言，放门口", Sound = AlertSound.Remark },
                new AlertSpeechFollowup { Text = "卖家备注，检查颜色", Sound = AlertSound.Remark }
            ]
        };

        Assert.Equal(AlertPublishResult.Accepted, alerts.Publish(refundAlert));
        Assert.NotNull(playedRequest);
        Assert.Equal(AlertSound.IndustrialAlarm, playedRequest.Sound);
        Assert.Equal(1, playedRequest.SoundRepeatCount);
        Assert.Equal(3, playedRequest.SpeechRepeatCount);
        Assert.Equal(2, playedRequest.FollowupSpeech.Count);
        Assert.Equal("买家留言，放门口", playedRequest.FollowupSpeech[0].Text);
        Assert.Equal("卖家备注，检查颜色", playedRequest.FollowupSpeech[1].Text);
    }

    [Fact]
    public void AlertService_VoiceOnlyRequestDoesNotReplaceVisualMessage()
    {
        int presentationCount = 0;
        AlertRequest? playedRequest = null;
        using var alerts = new AlertService(
            _ => presentationCount++,
            request => playedRequest = request);

        alerts.Publish(new AlertRequest
        {
            SpeechText = "开始录制",
            VoiceStyle = AlertVoiceStyle.Normal,
            Sound = AlertSound.None,
            DisplayDuration = TimeSpan.Zero
        });

        Assert.Equal(0, presentationCount);
        Assert.NotNull(playedRequest);
        Assert.Equal(AlertVoiceStyle.Normal, playedRequest.VoiceStyle);
        Assert.Equal(AlertSound.None, playedRequest.Sound);
    }

    [Fact]
    public void DefaultSpeechCatalog_ContainsAllFixedRefundAnnouncementsWithoutDuplicates()
    {
        string[] expectedRefundPrompts =
        [
            DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(DefaultSpeechCatalog.RefundWaitingSeller),
            DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(DefaultSpeechCatalog.RefundWaitingBuyerReturn),
            DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(DefaultSpeechCatalog.RefundWaitingSellerConfirm),
            DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(DefaultSpeechCatalog.RefundCompleted),
            DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(DefaultSpeechCatalog.RefundClosed)
        ];

        Assert.Equal(
            DefaultSpeechCatalog.Prompts.Count,
            DefaultSpeechCatalog.Prompts.Select(prompt => (prompt.Text, prompt.VoiceStyle)).Distinct().Count());
        foreach (string prompt in expectedRefundPrompts)
        {
            Assert.Contains(
                DefaultSpeechCatalog.Prompts,
                item => item.Text == prompt && item.VoiceStyle == AlertVoiceStyle.Warning);
        }
        Assert.DoesNotContain(DefaultSpeechCatalog.Prompts, item => item.Text == "摄像头已唤醒");
        Assert.DoesNotContain(DefaultSpeechCatalog.Prompts, item => item.Text == "摄像头已休眠");
    }
}
