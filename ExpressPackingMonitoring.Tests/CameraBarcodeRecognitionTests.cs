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
    public void StabilityTracker_ShortLossDoesNotRearmLockedCode()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(1));
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(1.2));

        Assert.Empty(held.CandidateCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_RemovalForRearmDelayAllowsSameCodeAgain()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(2));
        CameraBarcodeObservation candidate = tracker.Observe("YT123456789012", Start.AddSeconds(2.1));
        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddSeconds(2.35));

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
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

    [Fact]
    public void Decoder_GuideRegionRecognizesCode128()
    {
        using Mat frame = CreateFrameWithBarcode("YT123456789012", BarcodeFormat.CODE_128, inGuide: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void Decoder_FullFrameFallbackFindsBarcodeOutsideGuide()
    {
        using Mat frame = CreateFrameWithBarcode("SF123456789012", BarcodeFormat.CODE_128, inGuide: false);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Equal("SF123456789012", decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_GuideRegionRecognizesRotatedBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("JD123456789012", BarcodeFormat.CODE_128, inGuide: true, rotate90: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("JD123456789012", decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void Decoder_DoesNotAcceptEanProductBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("6901234567892", BarcodeFormat.EAN_13, inGuide: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Null(decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_DoesNotAcceptUpcProductBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("012345678905", BarcodeFormat.UPC_A, inGuide: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Null(decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public async Task RecognitionService_RecordingGateBlocksFullFrameFallback()
    {
        using Mat frame = CreateFrameWithBarcode("ZT123456789012", BarcodeFormat.CODE_128, inGuide: false);
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
        Assert.True(config.EnableGlobalKeyboard);
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
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
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
                Height = rotate90 ? 80 : 120,
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
        int x = (frame.Width - oriented.Width) / 2;
        int y = inGuide ? (frame.Height - oriented.Height) / 2 : 20;
        using Mat target = frame.SubMat(new Rect(x, y, oriented.Width, oriented.Height));
        oriented.CopyTo(target);
        return frame;
    }
}
