using ExpressPackingMonitoring.Services;
using System.Collections.Generic;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.Audio
{
    public sealed record DefaultSpeechPrompt(string Text, AlertVoiceStyle VoiceStyle);

    public static class DefaultSpeechCatalog
    {
        public static string StartRecording => T("StartRecording");
        public static string StopRecording => T("StopRecording");
        public static string SwitchToShipping => T("SwitchToShipping");
        public static string SwitchToReturn => T("SwitchToReturn");
        public static string CameraConnected => T("CameraConnected");
        public static string MotionDetected => T("MotionDetected");
        public static string TestOrderReceived => T("TestOrderReceived");

        public static string MissingOrderNumber => T("MissingOrderNumber");
        public static string InvalidOrderNumber => T("InvalidOrderNumber");
        public static string DuplicateOrderNumber => T("DuplicateOrderNumber");
        public static string RecordingHasNoOrderNumber => T("RecordingHasNoOrderNumber");
        public static string OrderNumberMismatch => T("OrderNumberMismatch");
        public static string VideoFileTooSmall => T("VideoFileTooSmall");
        public static string RecordingTooShort => T("RecordingTooShort");
        public static string CameraNotReady => T("CameraNotReady");
        public static string StoragePathNotWritable => T("StoragePathNotWritable");
        public static string AudioRecordingStartFailed => T("AudioRecordingStartFailed");
        public static string RecordingFailed => T("RecordingFailed");
        public static string ReconnectCamera => T("ReconnectCamera");
        public static string CameraDisconnected => T("CameraDisconnected");
        public static string CameraNotDetected => T("CameraNotDetected");
        public static string CameraReconnecting => T("CameraReconnecting");
        public static string MotionTimeoutWarning => T("MotionTimeoutWarning");
        public static string RecordingDurationWarning => T("RecordingDurationWarning");
        public static string MotionTimeoutStopped => T("MotionTimeoutStopped");
        public static string RecordingDurationStopped => T("RecordingDurationStopped");

        public static string RefundWaitingSeller => T("RefundWaitingSeller");
        public static string RefundWaitingBuyerReturn => T("RefundWaitingBuyerReturn");
        public static string RefundWaitingSellerConfirm => T("RefundWaitingSellerConfirm");
        public static string RefundCompleted => T("RefundCompleted");
        public static string RefundClosed => T("RefundClosed");

        public static string CreatePrintedRefundAnnouncement(string statusText) =>
            AppLanguage.Format("Speech.PrintedRefund", statusText);

        public static string CreateBuyerMessageAnnouncement(string message) => AppLanguage.Format("Speech.BuyerMessage", message);

        public static string CreateSellerMemoAnnouncement(string memo) => AppLanguage.Format("Speech.SellerMemo", memo);

        public static string CreateProductAnnouncement(string productInfo) => AppLanguage.Format("Speech.Product", productInfo);

        private static string T(string key) => AppLanguage.Get("Speech." + key);

        public static IReadOnlyList<DefaultSpeechPrompt> Prompts =>
        [
            new(StartRecording, AlertVoiceStyle.Normal),
            new(StopRecording, AlertVoiceStyle.Normal),
            new(SwitchToShipping, AlertVoiceStyle.Normal),
            new(SwitchToReturn, AlertVoiceStyle.Normal),
            new(CameraConnected, AlertVoiceStyle.Normal),
            new(MotionDetected, AlertVoiceStyle.Normal),
            new(TestOrderReceived, AlertVoiceStyle.Normal),

            new(MissingOrderNumber, AlertVoiceStyle.Warning),
            new(InvalidOrderNumber, AlertVoiceStyle.Warning),
            new(DuplicateOrderNumber, AlertVoiceStyle.Warning),
            new(RecordingHasNoOrderNumber, AlertVoiceStyle.Warning),
            new(OrderNumberMismatch, AlertVoiceStyle.Warning),
            new(VideoFileTooSmall, AlertVoiceStyle.Warning),
            new(RecordingTooShort, AlertVoiceStyle.Warning),
            new(CameraNotReady, AlertVoiceStyle.Warning),
            new(StoragePathNotWritable, AlertVoiceStyle.Warning),
            new(AudioRecordingStartFailed, AlertVoiceStyle.Warning),
            new(RecordingFailed, AlertVoiceStyle.Warning),
            new(ReconnectCamera, AlertVoiceStyle.Warning),
            new(CameraDisconnected, AlertVoiceStyle.Warning),
            new(CameraNotDetected, AlertVoiceStyle.Warning),
            new(CameraReconnecting, AlertVoiceStyle.Warning),
            new(MotionTimeoutWarning, AlertVoiceStyle.Warning),
            new(RecordingDurationWarning, AlertVoiceStyle.Warning),
            new(MotionTimeoutStopped, AlertVoiceStyle.Warning),
            new(RecordingDurationStopped, AlertVoiceStyle.Warning),

            new(CreatePrintedRefundAnnouncement(RefundWaitingSeller), AlertVoiceStyle.Warning),
            new(CreatePrintedRefundAnnouncement(RefundWaitingBuyerReturn), AlertVoiceStyle.Warning),
            new(CreatePrintedRefundAnnouncement(RefundWaitingSellerConfirm), AlertVoiceStyle.Warning),
            new(CreatePrintedRefundAnnouncement(RefundCompleted), AlertVoiceStyle.Warning),
            new(CreatePrintedRefundAnnouncement(RefundClosed), AlertVoiceStyle.Warning)
        ];
    }
}
