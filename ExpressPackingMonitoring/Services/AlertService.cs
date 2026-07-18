using System;
using System.Collections.Generic;

namespace ExpressPackingMonitoring.Services
{
    public enum AlertPriority
    {
        Normal = 0,
        Critical = 100
    }

    public enum AlertSound
    {
        None,
        Warning,
        Remark,
        IndustrialAlarm
    }

    public enum AlertVoiceStyle
    {
        Normal,
        Warning
    }

    public sealed class AlertSpeechFollowup
    {
        public string Text { get; init; } = string.Empty;
        public AlertVoiceStyle VoiceStyle { get; init; } = AlertVoiceStyle.Normal;
        public AlertSound Sound { get; init; } = AlertSound.None;
    }

    public sealed class AlertRequest
    {
        public string Message { get; init; } = string.Empty;
        public string SpeechText { get; init; } = string.Empty;
        public AlertPriority Priority { get; init; } = AlertPriority.Normal;
        public AlertSound Sound { get; init; } = AlertSound.Warning;
        public AlertVoiceStyle VoiceStyle { get; init; } = AlertVoiceStyle.Warning;
        public int SoundRepeatCount { get; init; } = 1;
        public int SpeechRepeatCount { get; init; } = 1;
        public bool InterruptCurrent { get; init; } = true;
        public TimeSpan DisplayDuration { get; init; } = TimeSpan.FromMilliseconds(2500);
        public string DeduplicationKey { get; init; } = string.Empty;
        public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromSeconds(3);
        public IReadOnlyList<AlertSpeechFollowup> FollowupSpeech { get; init; } = Array.Empty<AlertSpeechFollowup>();
    }

    public enum AlertPublishResult
    {
        Accepted,
        DroppedAsDuplicate,
        DroppedByHigherPriority
    }

    /// <summary>
    /// 统一管理告警优先级、界面展示、音效/语音参数和短时间去重。
    /// 具体的 WPF 展示与音频播放由调用方注入，便于独立测试告警策略。
    /// </summary>
    public sealed class AlertService : IDisposable
    {
        private readonly object _sync = new();
        private readonly Action<AlertRequest> _present;
        private readonly Action<AlertRequest> _playAudio;
        private readonly Action _interruptAudio;
        private readonly Action<string, AlertVoiceStyle> _preGenerate;
        private readonly Action _pauseAudio;
        private readonly Action _resumeAudio;
        private readonly Func<DateTime> _utcNow;
        private readonly Dictionary<string, DateTime> _deduplicationExpirations = new(StringComparer.Ordinal);
        private AlertPriority _activePriority = AlertPriority.Normal;
        private DateTime _activeUntilUtc = DateTime.MinValue;
        private bool _disposed;

        public AlertService(
            Action<AlertRequest> present,
            Action<AlertRequest> playAudio,
            Func<DateTime>? utcNow = null,
            Action? interruptAudio = null,
            Action<string, AlertVoiceStyle>? preGenerate = null,
            Action? pauseAudio = null,
            Action? resumeAudio = null)
        {
            _present = present ?? throw new ArgumentNullException(nameof(present));
            _playAudio = playAudio ?? throw new ArgumentNullException(nameof(playAudio));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _interruptAudio = interruptAudio ?? (() => { });
            _preGenerate = preGenerate ?? ((_, _) => { });
            _pauseAudio = pauseAudio ?? (() => { });
            _resumeAudio = resumeAudio ?? (() => { });
        }

        public AlertPublishResult Publish(AlertRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            lock (_sync)
            {
                if (_disposed)
                    return AlertPublishResult.DroppedByHigherPriority;

                DateTime now = _utcNow();
                RemoveExpiredDeduplicationKeys(now);

                if (!string.IsNullOrWhiteSpace(request.DeduplicationKey) &&
                    _deduplicationExpirations.TryGetValue(request.DeduplicationKey, out DateTime expiresAt) &&
                    expiresAt > now)
                {
                    return AlertPublishResult.DroppedAsDuplicate;
                }

                if (_activeUntilUtc > now && request.Priority < _activePriority)
                    return AlertPublishResult.DroppedByHigherPriority;

                if (!string.IsNullOrWhiteSpace(request.DeduplicationKey))
                    _deduplicationExpirations[request.DeduplicationKey] = now + request.DeduplicationWindow;

                _activePriority = request.Priority;
                _activeUntilUtc = now + request.DisplayDuration;
            }

            if (!string.IsNullOrWhiteSpace(request.Message))
            {
                try { _present(request); } catch { }
            }
            if (!string.IsNullOrWhiteSpace(request.SpeechText))
            {
                try { _playAudio(request); } catch { }
            }
            return AlertPublishResult.Accepted;
        }

        public void InterruptAudio() => _interruptAudio();

        public void PreGenerate(string text, AlertVoiceStyle voiceStyle = AlertVoiceStyle.Normal)
        {
            if (!string.IsNullOrWhiteSpace(text))
                _preGenerate(text, voiceStyle);
        }

        public void PauseAudio() => _pauseAudio();

        public void ResumeAudio() => _resumeAudio();

        private void RemoveExpiredDeduplicationKeys(DateTime now)
        {
            if (_deduplicationExpirations.Count == 0)
                return;

            var expiredKeys = new List<string>();
            foreach ((string key, DateTime expiresAt) in _deduplicationExpirations)
            {
                if (expiresAt <= now)
                    expiredKeys.Add(key);
            }

            foreach (string key in expiredKeys)
                _deduplicationExpirations.Remove(key);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _deduplicationExpirations.Clear();
            }
        }
    }
}
