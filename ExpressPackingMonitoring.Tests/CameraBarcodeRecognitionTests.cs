using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using System.Text.Json;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraBarcodeRecognitionTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StabilityTracker_SingleHitDoesNotConfirm()
    {
        var tracker = new CameraBarcodeStabilityTracker();

        CameraBarcodeObservation observation = tracker.Observe("YT123456789012", Start);

        Assert.Equal("YT123456789012", observation.CandidateCode);
        Assert.Empty(observation.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_TwoHitsWithinWindowConfirmOnce()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        tracker.Observe("YT123456789012", Start);

        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddMilliseconds(250));
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(1));

        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_StartConfirmationRestartsAfterMissedDetection()
    {
        var tracker = new CameraBarcodeStabilityTracker();

        tracker.Observe("YT123456789012", Start);
        tracker.Observe(null, Start.AddMilliseconds(100));
        CameraBarcodeObservation restarted = tracker.Observe(
            "YT123456789012",
            Start.AddMilliseconds(250));
        CameraBarcodeObservation confirmed = tracker.Observe(
            "YT123456789012",
            Start.AddMilliseconds(500));

        Assert.Equal("YT123456789012", restarted.CandidateCode);
        Assert.Empty(restarted.ConfirmedCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_IntermittentWindowConfirmsOnThirdHit()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        TimeSpan confirmationWindow = TimeSpan.FromSeconds(2);

        tracker.Observe("YT123456789012", Start, confirmationWindow);
        CameraBarcodeObservation secondHit = tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(0.75),
            confirmationWindow);
        CameraBarcodeObservation thirdHit = tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(1.5),
            confirmationWindow);

        Assert.Equal("YT123456789012", secondHit.CandidateCode);
        Assert.True(secondHit.KeepDecoding);
        Assert.Empty(secondHit.ConfirmedCode);
        Assert.Equal("YT123456789012", thirdHit.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_IntermittentWindowKeepsHitsAcrossMissedDetections()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        TimeSpan confirmationWindow = TimeSpan.FromSeconds(2);

        tracker.Observe("YT123456789012", Start, confirmationWindow);
        CameraBarcodeObservation missed = tracker.Observe(
            null,
            Start.AddSeconds(0.5),
            confirmationWindow);
        tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(1),
            confirmationWindow);
        tracker.Observe(null, Start.AddSeconds(1.25), confirmationWindow);
        CameraBarcodeObservation confirmed = tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(1.75),
            confirmationWindow);

        Assert.Equal("YT123456789012", missed.CandidateCode);
        Assert.True(missed.KeepDecoding);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_IntermittentWindowRestartsAfterWindowExpires()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        TimeSpan confirmationWindow = TimeSpan.FromSeconds(1.5);

        tracker.Observe("YT123456789012", Start, confirmationWindow);
        tracker.Observe("YT123456789012", Start.AddSeconds(0.5), confirmationWindow);
        CameraBarcodeObservation restarted = tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(1.6),
            confirmationWindow);
        tracker.Observe("YT123456789012", Start.AddSeconds(1.9), confirmationWindow);
        CameraBarcodeObservation confirmed = tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(2.2),
            confirmationWindow);

        Assert.Equal("YT123456789012", restarted.CandidateCode);
        Assert.Empty(restarted.ConfirmedCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_BusyResetPreservesConfirmedCodeDebounce()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Reset(preserveLockedCodes: true);
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(2));

        Assert.Empty(held.CandidateCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_FullResetAllowsSameCodeAgain()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Reset();
        CameraBarcodeObservation candidate = tracker.Observe("YT123456789012", Start.AddSeconds(2));
        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddSeconds(2.25));

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_ShortLossDoesNotRearmLockedCode()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(0.5));
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(3.4));

        Assert.Empty(held.CandidateCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_RemovalForRearmDelayAllowsSameCodeAgain()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(0.5));
        tracker.Observe(null, Start.AddSeconds(3.5));
        CameraBarcodeObservation candidate = tracker.Observe("YT123456789012", Start.AddSeconds(3.6));
        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddSeconds(3.85));

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_FirstReappearanceAfterRearmDelayUnlocksSameCode()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(0.5));
        CameraBarcodeObservation candidate = tracker.Observe("YT123456789012", Start.AddSeconds(3.5));
        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddSeconds(3.75));

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_CustomRearmDelayControlsWhenSameCodeCanReturn()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");
        TimeSpan rearmDelay = TimeSpan.FromSeconds(5);

        tracker.Observe(null, Start.AddSeconds(0.5), rearmDelay: rearmDelay);
        tracker.Observe(null, Start.AddSeconds(5.4), rearmDelay: rearmDelay);
        CameraBarcodeObservation candidate = tracker.Observe(
            "YT123456789012",
            Start.AddSeconds(5.5),
            rearmDelay: rearmDelay);

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Empty(candidate.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_DifferentCodeCanConfirmWithoutWaitingForOldCodeToRearm()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        CameraBarcodeObservation candidate = tracker.Observe("SF123456789012", Start.AddMilliseconds(500));
        CameraBarcodeObservation confirmed = tracker.Observe("SF123456789012", Start.AddMilliseconds(750));

        Assert.Equal("SF123456789012", candidate.CandidateCode);
        Assert.Equal("SF123456789012", confirmed.ConfirmedCode);
    }

    [Theory]
    [InlineData("YT123456789012", true)]
    [InlineData("STARTSTARTSTART", false)]
    [InlineData("SHIP123456789", false)]
    [InlineData("BACK123456789", false)]
    [InlineData("STOP123456789", false)]
    [InlineData("123", false)]
    public void CandidatePolicy_RejectsCommandsAndInvalidOrderNumbers(string value, bool expected)
    {
        Assert.Equal(expected, CameraBarcodeCandidatePolicy.IsValid(value, "^[a-zA-Z0-9-]{12,25}$"));
    }

    [Theory]
    [InlineData("YT123456789012", "YT123456789012", true, true)]
    [InlineData(" yt123456789012 ", "YT123456789012", true, true)]
    [InlineData("YT123456789012", "SF123456789012", true, false)]
    [InlineData("YT123456789012", "YT123456789012", false, false)]
    public void CandidatePolicy_CurrentRecordingCodeIsIgnoredOnlyWhileRecording(
        string value,
        string recordingOrderId,
        bool isRecording,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeCandidatePolicy.IsCurrentRecordingCode(value, recordingOrderId, isRecording));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CandidatePolicy_CurrentCameraCodeCanReachStopOnlyWhenSameCodeStopIsEnabled(
        bool sameBarcodeStopEnabled,
        bool expectedIgnored)
    {
        Assert.Equal(
            expectedIgnored,
            CameraBarcodeCandidatePolicy.ShouldIgnoreCurrentRecordingCode(
                "YT123456789012",
                "YT123456789012",
                isRecording: true,
                sameBarcodeStopEnabled));
    }

    [Theory]
    [InlineData(false, "", false, false, "Start")]
    [InlineData(true, "YT123456789012", false, false, "Ignore")]
    [InlineData(true, "YT123456789012", true, false, "Stop")]
    [InlineData(true, "SF123456789012", true, false, "Ignore")]
    [InlineData(false, "", false, true, "Queue")]
    public void RecordingDecisionPolicy_CameraPreviewMatchesRecordingRules(
        bool isRecording,
        string recordingOrderId,
        bool sameBarcodeStopEnabled,
        bool inputOnCooldown,
        string expected)
    {
        BarcodeRecordingDecision decision = BarcodeRecordingDecisionPolicy.Evaluate(
            "YT123456789012",
            fromCamera: true,
            canProcess: true,
            isRecording,
            recordingOrderId,
            sameBarcodeStopEnabled,
            inputOnCooldown,
            "^[a-zA-Z0-9-]{12,25}$");

        Assert.Equal(expected, decision.Action.ToString());
    }

    [Fact]
    public void RecordingDecisionPolicy_ScannerStillSwitchesRecordingInContinuousMode()
    {
        BarcodeRecordingDecision decision = BarcodeRecordingDecisionPolicy.Evaluate(
            "YT123456789012",
            fromCamera: false,
            canProcess: true,
            isRecording: true,
            recordingOrderId: "YT123456789012",
            sameBarcodeStopEnabled: false,
            inputOnCooldown: false,
            "^[a-zA-Z0-9-]{12,25}$");

        Assert.Equal(BarcodeRecordingDecisionAction.Switch, decision.Action);
    }

    [Theory]
    [InlineData("CLEAR", "ClearInput", "ClearCommand")]
    [InlineData("SHIP", "SwitchToShipping", "ShippingCommand")]
    [InlineData("BACK", "SwitchToReturn", "ReturnCommand")]
    [InlineData("START", "ToggleRecording", "StartCommand")]
    [InlineData("STOP", "Stop", "StopCommand")]
    public void RecordingDecisionPolicy_CommandsHaveExecutableDecisions(
        string scan,
        string expectedAction,
        string expectedReason)
    {
        BarcodeRecordingDecision decision = BarcodeRecordingDecisionPolicy.Evaluate(
            scan,
            fromCamera: false,
            canProcess: true,
            isRecording: false,
            recordingOrderId: "",
            sameBarcodeStopEnabled: false,
            inputOnCooldown: false,
            "^[a-zA-Z0-9-]{12,25}$");

        Assert.Equal(expectedAction, decision.Action.ToString());
        Assert.Equal(expectedReason, decision.Reason.ToString());
    }

    [Theory]
    [InlineData(new[] { "--camera-barcode-shadow" }, null, true)]
    [InlineData(new string[0], "true", true)]
    [InlineData(new string[0], "0", false)]
    [InlineData(new[] { "--other-option" }, null, false)]
    public void RuntimeOptions_ShadowModeRequiresExplicitOptIn(
        string[] arguments,
        string? environmentValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeRuntimeOptions.IsShadowModeEnabled(arguments, environmentValue));
    }

    [Fact]
    public void Decoder_GuideRegionRecognizesCode128()
    {
        using Mat frame = CreateFrameWithBarcode("YT123456789012", BarcodeFormat.CODE_128, inGuide: true);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void Decoder_GuideRegionCoversMostOfFrameWithoutBecomingFullFrame()
    {
        Rect guide = CameraBarcodeFrameDecoder.GetGuideRect(1280, 720);

        Assert.Equal(new Rect(96, 54, 1088, 612), guide);
        Assert.True(guide.Width < 1280);
        Assert.True(guide.Height < 720);
    }

    [Fact]
    public void Decoder_FullFrameFallbackFindsBarcodeOutsideGuide()
    {
        using Mat frame = CreateFrameWithBarcode(
            "SF123456789012",
            BarcodeFormat.CODE_128,
            inGuide: false,
            rotate90: true);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Equal("SF123456789012", decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_GuideRegionRecognizesRotatedBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("JD123456789012", BarcodeFormat.CODE_128, inGuide: true, rotate90: true);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("JD123456789012", decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void Decoder_DoesNotAcceptEanProductBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("6901234567892", BarcodeFormat.EAN_13, inGuide: true);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Null(decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_DoesNotAcceptUpcProductBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("012345678905", BarcodeFormat.UPC_A, inGuide: true);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Null(decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_ReusesLargePixelBuffersForStableFrameSize()
    {
        using Mat frame = CreateFrameWithBarcode("YT123456789012", BarcodeFormat.CODE_128, inGuide: true);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));
        int allocationsAfterWarmup = decoder.PixelBufferAllocationCount;
        Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));

        Assert.Equal(1, allocationsAfterWarmup);
        Assert.Equal(allocationsAfterWarmup, decoder.PixelBufferAllocationCount);
    }

    [Fact]
    public void Decoder_RepeatedDecodesAvoidPerFrameLargeManagedAllocations()
    {
        using Mat frame = CreateFrameWithBarcode("YT123456789012", BarcodeFormat.CODE_128, inGuide: true);
        using var decoder = new CameraBarcodeFrameDecoder();
        Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));

        const int iterations = 8;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
            Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < 4_000_000,
            $"Repeated decoding allocated {allocated:N0} managed bytes; large per-frame buffers may have returned.");
    }

    [Fact]
    public void MotionGate_StableSceneSleepsUntilVisibleChangeOccurs()
    {
        using var gate = new CameraBarcodeMotionGate();
        using Mat stillFrame = CreateSolidFrame(100);
        using Mat changedFrame = CreateSolidFrame(100);
        Cv2.Rectangle(changedFrame, new Rect(320, 180, 640, 360), Scalar.Black, thickness: -1);

        Assert.True(gate.ShouldDecode(stillFrame, Start));
        Assert.True(gate.ShouldDecode(stillFrame, Start.AddMilliseconds(500)));
        Assert.False(gate.ShouldDecode(stillFrame, Start.AddSeconds(1.25)));

        Assert.True(gate.ShouldDecode(changedFrame, Start.AddSeconds(1.5)));
        Assert.True(gate.ShouldDecode(changedFrame, Start.AddSeconds(2.25)));
        Assert.False(gate.ShouldDecode(changedFrame, Start.AddSeconds(2.75)));
    }

    [Fact]
    public void MotionGate_MinorCameraNoiseDoesNotWakeRecognition()
    {
        using var gate = new CameraBarcodeMotionGate();
        using Mat baseline = CreateSolidFrame(100);
        using Mat minorNoise = CreateSolidFrame(104);

        Assert.True(gate.ShouldDecode(baseline, Start));
        Assert.False(gate.ShouldDecode(minorNoise, Start.AddSeconds(2)));
    }

    [Fact]
    public void MotionGate_ShippingLabelEnteringStableSceneWakesRecognition()
    {
        using var gate = new CameraBarcodeMotionGate();
        using Mat emptyFrame = CreateSolidFrame(255);
        using Mat labelFrame = CreateFrameWithBarcode(
            "YT123456789012",
            BarcodeFormat.CODE_128,
            inGuide: true);

        Assert.True(gate.ShouldDecode(emptyFrame, Start));
        Assert.False(gate.ShouldDecode(emptyFrame, Start.AddSeconds(2)));

        Assert.True(gate.ShouldDecode(labelFrame, Start.AddSeconds(2.25)));
    }

    [Fact]
    public void MotionGate_ResetMakesNextFrameEligibleForRecognition()
    {
        using var gate = new CameraBarcodeMotionGate();
        using Mat frame = CreateSolidFrame(100);

        Assert.True(gate.ShouldDecode(frame, Start));
        Assert.False(gate.ShouldDecode(frame, Start.AddSeconds(2)));

        gate.Reset();

        Assert.True(gate.ShouldDecode(frame, Start.AddSeconds(2.25)));
    }

    [Fact]
    public void MotionGate_ContinuousCandidateKeepsStableSceneDecoding()
    {
        using var gate = new CameraBarcodeMotionGate();
        using Mat frame = CreateSolidFrame(100);

        Assert.True(gate.ShouldDecode(frame, Start));
        Assert.True(gate.ShouldDecode(frame, Start.AddSeconds(2), forceDecode: true));
    }

    [Fact]
    public async Task RecognitionService_RecordingGateBlocksFullFrameFallback()
    {
        using Mat frame = CreateFrameWithBarcode(
            "ZT123456789012",
            BarcodeFormat.CODE_128,
            inGuide: false,
            rotate90: true);
        using var service = new CameraBarcodeRecognitionService(
            value => CameraBarcodeCandidatePolicy.IsValid(value, "^[a-zA-Z0-9-]{12,25}$"),
            fullFrameAllowed: () => false);
        int confirmedCount = 0;
        service.BarcodeConfirmed += _ => Interlocked.Increment(ref confirmedCount);

        service.TrySubmitFrame(frame, allowFullFrame: true);
        await Task.Delay(950, TestContext.Current.CancellationToken);
        service.TrySubmitFrame(frame, allowFullFrame: true);
        await Task.Delay(350, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref confirmedCount));
    }

    [Fact]
    public void ExistingConfigWithoutCameraRecognitionFieldRemainsDisabled()
    {
        const string json = "{\"FirstUseWizardCompleted\":true,\"EnableGlobalKeyboard\":true}";

        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json);

        Assert.NotNull(config);
        Assert.False(config.EnableCameraBarcodeRecognition);
        Assert.Equal(0, config.CameraBarcodeSetupVersion);
        Assert.Equal(3.0, config.CameraBarcodeRearmSeconds);
        Assert.Equal(1.5, config.CameraSameBarcodeConfirmationSeconds);
        Assert.True(config.EnableGlobalKeyboard);
    }

    [Fact]
    public void NormalizeAfterLoad_ClampsCameraSameCodeTimingSettings()
    {
        var config = new AppConfig
        {
            CameraBarcodeRearmSeconds = 0,
            CameraSameBarcodeConfirmationSeconds = 100
        };

        bool changed = AppConfig.NormalizeAfterLoad(config);

        Assert.True(changed);
        Assert.Equal(1.0, config.CameraBarcodeRearmSeconds);
        Assert.Equal(10.0, config.CameraSameBarcodeConfirmationSeconds);
    }

    [Fact]
    public void FirstUseDefaultsEnableCameraRecognitionWithoutChangingScannerMode()
    {
        var config = new AppConfig
        {
            EnableGlobalKeyboard = false,
            EnableScannerAutoSubmit = true
        };

        AppConfig.ApplyFirstUseDefaults(config);

        Assert.True(config.FirstUseWizardCompleted);
        Assert.True(config.EnableCameraBarcodeRecognition);
        Assert.Equal(AppConfig.CurrentCameraBarcodeSetupVersion, config.CameraBarcodeSetupVersion);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    public void CameraBarcodeUpgradePrompt_OnlyShowsOnceForExistingUsers(
        bool firstUseCompleted,
        int setupVersion,
        bool expected)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = firstUseCompleted,
            CameraBarcodeSetupVersion = setupVersion
        };

        Assert.Equal(expected, AppConfig.ShouldPromptCameraBarcodeUpgrade(config));
    }

    [Fact]
    public void CameraBarcodeUpgradeChoice_EnablePreservesScannerSettings()
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableCameraBarcodeRecognition = false,
            EnableGlobalKeyboard = false,
            EnableScannerAutoSubmit = true
        };

        AppConfig.ApplyCameraBarcodeUpgradeChoice(config, enableRecognition: true);

        Assert.True(config.EnableCameraBarcodeRecognition);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
        Assert.Equal(AppConfig.CurrentCameraBarcodeSetupVersion, config.CameraBarcodeSetupVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraBarcodeUpgradeChoice_DeclinePreservesCurrentRecognitionAndScannerSettings(bool recognitionEnabled)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableCameraBarcodeRecognition = recognitionEnabled,
            EnableGlobalKeyboard = true,
            EnableScannerAutoSubmit = false
        };

        AppConfig.ApplyCameraBarcodeUpgradeChoice(config, enableRecognition: false);

        Assert.Equal(recognitionEnabled, config.EnableCameraBarcodeRecognition);
        Assert.True(config.EnableGlobalKeyboard);
        Assert.False(config.EnableScannerAutoSubmit);
        Assert.Equal(AppConfig.CurrentCameraBarcodeSetupVersion, config.CameraBarcodeSetupVersion);
    }

    private static CameraBarcodeStabilityTracker Confirm(string trackingNumber)
    {
        var tracker = new CameraBarcodeStabilityTracker();
        tracker.Observe(trackingNumber, Start);
        tracker.Observe(trackingNumber, Start.AddMilliseconds(250));
        return tracker;
    }

    private static Mat CreateFrameWithBarcode(string value, BarcodeFormat format, bool inGuide, bool rotate90 = false)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = rotate90 ? 280 : 520,
                Height = rotate90 ? 56 : 120,
                Margin = 16,
                PureBarcode = true
            }
        };
        var pixels = writer.Write(value);
        using Mat bgra = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC4, pixels.Pixels).Clone();
        using Mat barcode = new();
        Cv2.CvtColor(bgra, barcode, ColorConversionCodes.BGRA2BGR);

        using Mat oriented = new();
        if (rotate90)
            Cv2.Rotate(barcode, oriented, RotateFlags.Rotate90Clockwise);
        else
            barcode.CopyTo(oriented);

        var frame = new Mat(new OpenCvSharp.Size(1280, 720), MatType.CV_8UC3, Scalar.White);
        int x = inGuide || !rotate90 ? (frame.Width - oriented.Width) / 2 : 0;
        int y = inGuide || rotate90 ? (frame.Height - oriented.Height) / 2 : 20;
        using Mat target = frame.SubMat(new Rect(x, y, oriented.Width, oriented.Height));
        oriented.CopyTo(target);
        return frame;
    }

    private static Mat CreateSolidFrame(byte value) =>
        new(new OpenCvSharp.Size(1280, 720), MatType.CV_8UC3, new Scalar(value, value, value));
}
