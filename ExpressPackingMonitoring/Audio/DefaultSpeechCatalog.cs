using ExpressPackingMonitoring.Services;
using System.Collections.Generic;

namespace ExpressPackingMonitoring.Audio
{
    public sealed record DefaultSpeechPrompt(string Text, AlertVoiceStyle VoiceStyle);

    public static class DefaultSpeechCatalog
    {
        public const string StartRecording = "开始录制";
        public const string StopRecording = "停止录制";
        public const string SwitchToShipping = "切换发货";
        public const string SwitchToReturn = "切换退货";
        public const string CameraConnected = "摄像头已连接";
        public const string MotionDetected = "检测到画面运动，重置超时";
        public const string TestOrderReceived = "收到测试订单";

        public const string MissingOrderNumber = "没有单号";
        public const string InvalidOrderNumber = "非法单号";
        public const string DuplicateOrderNumber = "重复单号";
        public const string RecordingHasNoOrderNumber = "当前录像未绑定单号";
        public const string OrderNumberMismatch = "单号不一致";
        public const string VideoFileTooSmall = "视频文件太小，已删除";
        public const string RecordingTooShort = "录像过短，已丢弃";
        public const string CameraNotReady = "摄像头未就绪";
        public const string StoragePathNotWritable = "存储路径不可写";
        public const string AudioRecordingStartFailed = "音频录制启动失败";
        public const string RecordingFailed = "录制失败";
        public const string ReconnectCamera = "请重新连接摄像头";
        public const string CameraDisconnected = "摄像头断开，正在尝试连接";
        public const string CameraNotDetected = "未检测到摄像头";
        public const string CameraReconnecting = "摄像头重新连接中";
        public const string MotionTimeoutWarning = "画面即将静止超时";
        public const string RecordingDurationWarning = "录制即将达到最大时长";
        public const string MotionTimeoutStopped = "静止超时，停止录制";
        public const string RecordingDurationStopped = "时长超时，停止录制";

        public const string RefundWaitingSeller = "等待卖家处理退款";
        public const string RefundWaitingBuyerReturn = "等待买家退货";
        public const string RefundWaitingSellerConfirm = "等待卖家确认收到退货";
        public const string RefundCompleted = "退款已完成";
        public const string RefundClosed = "退款流程已关闭或取消";

        public static string CreatePrintedRefundAnnouncement(string statusText) =>
            $"订单有退款，{statusText}，不要打包";

        public static string CreateBuyerMessageAnnouncement(string message) => $"买家留言，{message}";

        public static string CreateSellerMemoAnnouncement(string memo) => $"卖家备注，{memo}";

        public static string CreateProductAnnouncement(string productInfo) => $"商品，{productInfo}";

        public static IReadOnlyList<DefaultSpeechPrompt> Prompts { get; } =
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
