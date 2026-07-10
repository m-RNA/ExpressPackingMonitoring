using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using EdgeTTS;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;
using SherpaOnnx;

namespace ExpressPackingMonitoring.Audio
{
    public class SpeechRequest
    {
        public string Text { get; set; } = string.Empty;
        public bool IsWarning { get; set; }
        public int RepeatCount { get; set; } = 1;
        public bool PreferImmediateAiGeneration { get; set; }
        public bool PlayRemarkTone { get; set; }
    }

    public class SpeechService : IDisposable
    {
        private SpeechSynthesizer? _ttsNormal;
        private SpeechSynthesizer? _ttsWarning;
        private OfflineTts? _kokoroTts;
        private readonly object _kokoroLock = new();
        private BlockingCollection<SpeechRequest> _speechQueue = null!;
        private Thread _speechThread = null!;
        private volatile bool _speechCancelRequested;
        private volatile bool _isDisposed;
        private int _disposeStarted;
        private int _resourceCleanupStarted;
        private string? _ttsCacheDir;
        private DateTime _lastTtsUseTime = DateTime.MinValue;
        private Timer? _idleUnloadTimer;
        private BlockingCollection<(string text, bool isWarning)>? _preGenQueue;
        private Thread? _preGenThread;
        private readonly ManualResetEventSlim _speechProcessingGate = new(initialState: true);
        private readonly object _speechStateLock = new();
        private readonly object _filePlaybackLock = new();
        private IWavePlayer? _currentFileWaveOut;
        private volatile bool _pauseForRecordingRequested;
        private DateTime _lastCacheCleanupTime = DateTime.MinValue;
        private long _cacheBytesSinceCleanup;

        /// <summary>AI TTS 模型空闲多少分钟后自动卸载释放内存，0 = 不自动卸载</summary>
        public int AiTtsIdleUnloadMinutes { get; set; } = 1;

        /// <summary>TTS 缓存目录最大占用空间（MB），超出后按最久未访问清理，0 = 不限制</summary>
        public int TtsCacheMaxSizeMB { get; set; } = 500;

        public bool EnableSoundPrompt { get; set; } = true;
        public bool EnableAiTts { get; set; } = true;
        public string AiTtsEngine { get; set; } = "Edge";
        public int AiTtsSpeakerId { get; set; } = 51;
        public int AiTtsWarningSpeakerId { get; set; } = 50;
        public float AiTtsSpeed { get; set; } = 1.0f;
        public string EdgeTtsVoice { get; set; } = "zh-CN-XiaoxiaoNeural";
        public string EdgeTtsWarningVoice { get; set; } = "zh-CN-YunxiNeural";

        /// <summary>推理提供者：cpu / directml / cuda。切换 GPU 需要对应的 onnxruntime DLL</summary>
        public string AiTtsProvider { get; set; } = "cpu";

        /// <summary>AI TTS 模型是否已成功加载</summary>
        public bool IsAiTtsAvailable => IsEdgeTtsEngine || _kokoroTts != null;

        private bool IsEdgeTtsEngine => string.Equals(AiTtsEngine, "Edge", StringComparison.OrdinalIgnoreCase);

        public SpeechService()
        {
            InitSpeechSynthesizer();
        }

        public bool IsSpeechPaused => _pauseForRecordingRequested;
        public event Action<string>? PlaybackError;

        public void PauseForRecording()
        {
            if (_isDisposed) return;

            lock (_speechStateLock)
            {
                if (_pauseForRecordingRequested) return;
                _pauseForRecordingRequested = true;
                _speechProcessingGate.Reset(); // 只阻塞 AI 预生成线程
            }

            Debug.WriteLine("[SpeechService] 录制中，AI 语音生成已暂停，播放照常");
        }

        public void ResumeAfterRecording()
        {
            if (_isDisposed) return;

            lock (_speechStateLock)
            {
                if (!_pauseForRecordingRequested) return;
                _pauseForRecordingRequested = false;
                _speechProcessingGate.Set(); // 恢复 AI 预生成线程
            }

            Debug.WriteLine("[SpeechService] 录制结束，AI 语音生成已恢复");
        }

        private void WaitWhilePaused()
        {
            while (!_isDisposed && _pauseForRecordingRequested)
            {
                _speechProcessingGate.Wait(250);
            }
        }

        private void EnqueueSpeechRequest(SpeechRequest request)
        {
            if (_isDisposed) return;

            var queue = _speechQueue;
            if (queue == null || queue.IsAddingCompleted) return;

            try { queue.Add(request); } catch { }
        }


        private void InitSpeechSynthesizer()
        {
            _speechQueue = new BlockingCollection<SpeechRequest>();
            _speechThread = new Thread(SpeechThreadLoop) { IsBackground = true, Name = "SpeechThread" };
            _speechThread.Start();

            try
            {
                var voices = SpeechSynthesizer.AllVoices;
                var femaleZh = voices.FirstOrDefault(v => v != null && v.Gender == VoiceGender.Female && v.Language == "zh-CN");
                var maleZh = voices.FirstOrDefault(v => v != null && v.Gender == VoiceGender.Male && v.Language == "zh-CN");
                var anyZh = femaleZh ?? maleZh ?? voices.FirstOrDefault(v => v != null && v.Language.StartsWith("zh"));

                _ttsNormal = new SpeechSynthesizer();
                if (femaleZh != null) _ttsNormal.Voice = femaleZh;
                else if (anyZh != null) _ttsNormal.Voice = anyZh;

                _ttsWarning = new SpeechSynthesizer();
                if (maleZh != null) _ttsWarning.Voice = maleZh;
                else if (anyZh != null) _ttsWarning.Voice = anyZh;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Init failed: {ex.Message}");
                _ttsNormal = null;
                _ttsWarning = null;
            }
        }

        /// <summary>
        /// 初始化 Kokoro AI TTS 模型（需要在程序启动后调用一次）。
        /// 模型目录应位于 exe 同级的 kokoro-multi-lang-v1_0 文件夹下。
        /// </summary>
        public void InitAiTts(string? modelDir = null)
        {
            try
            {
                EnsureTtsCacheDir();
                if (IsEdgeTtsEngine)
                {
                    Debug.WriteLine("[SpeechService] Edge TTS enabled");
                    return;
                }

                if (modelDir == null)
                {
                    var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    modelDir = Path.Combine(exeDir, "kokoro-multi-lang-v1_0");
                }

                var modelPath = Path.Combine(modelDir, "model.onnx");
                if (!File.Exists(modelPath))
                {
                    Debug.WriteLine($"[SpeechService] Kokoro model not found: {modelPath}");
                    return;
                }

                var config = new OfflineTtsConfig();
                config.Model.Kokoro.Model = modelPath;
                config.Model.Kokoro.Voices = Path.Combine(modelDir, "voices.bin");
                config.Model.Kokoro.Tokens = Path.Combine(modelDir, "tokens.txt");
                config.Model.Kokoro.DataDir = Path.Combine(modelDir, "espeak-ng-data");

                // 中文词典
                var lexiconZh = Path.Combine(modelDir, "lexicon-zh.txt");
                var lexiconEn = Path.Combine(modelDir, "lexicon-us-en.txt");
                var lexicons = new List<string>();
                if (File.Exists(lexiconEn)) lexicons.Add(lexiconEn);
                if (File.Exists(lexiconZh)) lexicons.Add(lexiconZh);
                if (lexicons.Count > 0)
                    config.Model.Kokoro.Lexicon = string.Join(",", lexicons);

                var dictDir = Path.Combine(modelDir, "dict");
                if (Directory.Exists(dictDir))
                    config.Model.Kokoro.DictDir = dictDir;

                // 中文数字/日期 FST
                var ruleFsts = new List<string>();
                var dateFst = Path.Combine(modelDir, "date-zh.fst");
                var numberFst = Path.Combine(modelDir, "number-zh.fst");
                var phoneFst = Path.Combine(modelDir, "phone-zh.fst");
                if (File.Exists(dateFst)) ruleFsts.Add(dateFst);
                if (File.Exists(numberFst)) ruleFsts.Add(numberFst);
                if (File.Exists(phoneFst)) ruleFsts.Add(phoneFst);
                if (ruleFsts.Count > 0)
                    config.RuleFsts = string.Join(",", ruleFsts);

                config.Model.NumThreads = Math.Max(2, Environment.ProcessorCount / 3);
                config.Model.Provider = AiTtsProvider ?? "cpu";
                config.MaxNumSentences = 0; // 0 = 不限制句数，避免长文本被截断

                // 初始化磁盘缓存目录
                _ttsCacheDir = AppPaths.TtsCacheDir;
                Directory.CreateDirectory(_ttsCacheDir);

                _kokoroTts = new OfflineTts(config);
                _lastTtsUseTime = DateTime.Now;
                StartIdleUnloadTimer();
                Debug.WriteLine($"[SpeechService] Kokoro TTS loaded, speakers={_kokoroTts.NumSpeakers}, sampleRate={_kokoroTts.SampleRate}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Kokoro TTS init failed: {ex.Message}");
                _kokoroTts = null;
            }
        }

        private void SpeechThreadLoop()
        {
            try
            {
                foreach (var req in _speechQueue.GetConsumingEnumerable())
                {
                    if (_isDisposed) break;
                    _speechCancelRequested = false;

                    try
                    {
                        string fullText = req.RepeatCount > 1
                            ? string.Join("，", Enumerable.Repeat(req.Text, req.RepeatCount))
                            : req.Text;

                        if (req.IsWarning)
                        {
                            PlayWarningAlertToneBlocking();
                            if (_speechCancelRequested || _isDisposed) continue;
                        }
                        else if (req.PlayRemarkTone)
                        {
                            PlayRemarkToneBlocking();
                            if (_speechCancelRequested || _isDisposed) continue;
                        }

                        if (EnableAiTts)
                        {
                            SpeakWithAiTts(fullText, req.IsWarning, req.PreferImmediateAiGeneration);
                        }
                        else
                        {
                            SpeakWithWindowsTts(fullText, req.IsWarning);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SpeechService] Playback error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[SpeechService] Thread error: {ex.Message}"); }
        }

        private bool SpeakWithWindowsTts(string text, bool isWarning)
        {
            var synth = isWarning ? _ttsWarning : _ttsNormal;
            if (synth == null) return true;

            var result = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            result.AsStreamForRead().CopyTo(ms);
            var wavData = ms.ToArray();

            if (wavData.Length < 44) return true;
            if (_speechCancelRequested || _isDisposed) return true;

            PlayWavBlocking(wavData);
            return true;
        }

        private bool SpeakWithAiTts(string text, bool isWarning, bool preferImmediateGeneration)
        {
            text = PreprocessTextForTts(text);
            string voiceKey = GetCurrentVoiceKey(isWarning);
            string extension = IsEdgeTtsEngine ? ".mp3" : ".wav";

            Debug.WriteLine($"[SpeechService] {AiTtsEngine}: voice={voiceKey}, speed={AiTtsSpeed}, text=\"{text}\"");

            string cacheKey = GetCacheKey(text, AiTtsSpeed, voiceKey, AiTtsEngine);
            string cachePath = GetCachePath(cacheKey, extension);
            if (File.Exists(cachePath))
            {
                Debug.WriteLine($"[SpeechService] Cache HIT: {cacheKey}");
                if (!_speechCancelRequested && !_isDisposed)
                {
                    if (IsEdgeTtsEngine)
                        PlayAudioFileBlocking(cachePath);
                    else
                        PlayWavBlocking(File.ReadAllBytes(cachePath));
                }
                return true;
            }

            if (preferImmediateGeneration && TryGenerateAiCache(text, isWarning, cachePath))
            {
                if (!_speechCancelRequested && !_isDisposed)
                {
                    if (IsEdgeTtsEngine)
                        PlayAudioFileBlocking(cachePath);
                    else
                        PlayWavBlocking(File.ReadAllBytes(cachePath));
                }
                return true;
            }

            Debug.WriteLine("[SpeechService] Cache MISS, fallback to Windows TTS and generate AI cache in background");
            SpeakWithWindowsTts(text, isWarning);
            PreGenerateCacheInternal(text, isWarning);
            return true;
        }

        private bool TryGenerateAiCache(string text, bool isWarning, string cachePath)
        {
            try
            {
                WaitWhilePaused();
                if (_isDisposed || _speechCancelRequested) return false;

                if (IsEdgeTtsEngine)
                {
                    GenerateEdgeTtsFile(text, isWarning, cachePath);
                }
                else
                {
                    int sid = isWarning ? AiTtsWarningSpeakerId : AiTtsSpeakerId;
                    lock (_kokoroLock)
                    {
                        EnsureKokoroLoaded();
                        var tts = _kokoroTts;
                        if (tts == null) return false;
                        _lastTtsUseTime = DateTime.Now;

                        var audio = tts.Generate(text, AiTtsSpeed, sid);
                        if (audio == null || audio.NumSamples <= 0)
                        {
                            audio?.Dispose();
                            return false;
                        }

                        try
                        {
                            File.WriteAllBytes(cachePath, BuildWav16(audio.Samples, audio.SampleRate));
                        }
                        finally
                        {
                            audio.Dispose();
                        }
                    }
                }

                RequestCacheCleanup(cachePath);
                return File.Exists(cachePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Immediate AI generation error: {ex.Message}");
                PlaybackError?.Invoke(ex.Message);
                return false;
            }
        }
        private void GenerateEdgeTtsFile(string text, bool isWarning, string outputPath)
        {
            string voice = isWarning ? EdgeTtsWarningVoice : EdgeTtsVoice;
            if (string.IsNullOrWhiteSpace(voice))
                voice = isWarning ? "zh-CN-YunxiNeural" : "zh-CN-XiaoxiaoNeural";

            string tempPath = Path.Combine(
                Path.GetDirectoryName(outputPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                Path.GetFileNameWithoutExtension(outputPath) + ".tmp" + Path.GetExtension(outputPath));
            if (File.Exists(tempPath)) File.Delete(tempPath);

            var client = new EdgeTTSClient(false, false, 0);
            try
            {
                var result = client.SynthesisAsync(text, voice, "+0Hz", ToEdgeRate(AiTtsSpeed), "+0%")
                    .GetAwaiter()
                    .GetResult();

                if (result.Code != ResultCode.Success || result.Data == null || result.Data.Length == 0)
                    throw new InvalidOperationException($"Edge TTS failed: {result.Code} {result.Message}");

                File.WriteAllBytes(tempPath, result.Data.ToArray());
            }
            finally
            {
                try { client.Dispose(); } catch { }
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                throw new InvalidOperationException("Edge TTS did not create audio output.");

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempPath, outputPath);
            Debug.WriteLine($"[SpeechService] PreGenerate Edge cached: {Path.GetFileName(outputPath)} ({new FileInfo(outputPath).Length / 1024}KB)");
        }

        private void PlayAudioFileBlocking(string path)
        {
            if (!File.Exists(path)) return;

            WaveOutEvent? waveOut = null;
            AudioFileReader? reader = null;
            try
            {
                reader = new AudioFileReader(path);
                waveOut = new WaveOutEvent();
                waveOut.Init(reader);

                lock (_filePlaybackLock)
                {
                    _currentFileWaveOut?.Stop();
                    _currentFileWaveOut?.Dispose();
                    _currentFileWaveOut = waveOut;
                }

                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing && !_speechCancelRequested && !_isDisposed)
                {
                    Thread.Sleep(20);
                }

                if (_speechCancelRequested || _isDisposed)
                    waveOut.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Audio file playback error: {ex.Message}");
                PlaybackError?.Invoke(ex.Message);
            }
            finally
            {
                lock (_filePlaybackLock)
                {
                    if (ReferenceEquals(_currentFileWaveOut, waveOut))
                        _currentFileWaveOut = null;
                }

                try { waveOut?.Dispose(); } catch { }
                try { reader?.Dispose(); } catch { }
            }
        }
        #region TTS 磁盘缓存
        private static string GetCacheKey(string text, float speed, string voiceKey, string engine)
        {
            string raw = $"{engine}|{text}|{speed:F2}|{voiceKey}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }

        private void EnsureTtsCacheDir()
        {
            _ttsCacheDir ??= AppPaths.TtsCacheDir;
            Directory.CreateDirectory(_ttsCacheDir);
        }

        private string GetCachePath(string cacheKey, string extension)
        {
            EnsureTtsCacheDir();
            string path = Path.Combine(_ttsCacheDir!, cacheKey + extension);
            if (File.Exists(path))
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }

        private string GetCurrentVoiceKey(bool isWarning)
        {
            if (IsEdgeTtsEngine)
                return isWarning ? EdgeTtsWarningVoice : EdgeTtsVoice;
            return (isWarning ? AiTtsWarningSpeakerId : AiTtsSpeakerId).ToString();
        }

        private static string ToEdgeRate(float speed)
        {
            int percent = (int)Math.Round((Math.Clamp(speed, 0.5f, 2.0f) - 1.0f) * 100.0f);
            return percent >= 0 ? $"+{percent}%" : $"{percent}%";
        }

        private byte[]? LoadFromCache(string cacheKey)
        {
            if (_ttsCacheDir == null) return null;
            string path = Path.Combine(_ttsCacheDir, cacheKey + ".wav");
            if (!File.Exists(path)) return null;
            try
            {
                // 更新访问时间用于 LRU 清理
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Cache read error: {ex.Message}");
                return null;
            }
        }

        private void SaveToCache(string cacheKey, byte[] wavData)
        {
            if (_ttsCacheDir == null) return;
            try
            {
                string path = Path.Combine(_ttsCacheDir, cacheKey + ".wav");
                File.WriteAllBytes(path, wavData);
                Debug.WriteLine($"[SpeechService] Cached: {cacheKey}.wav ({wavData.Length / 1024}KB)");

                RequestCacheCleanup(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Cache write error: {ex.Message}");
            }
        }

        private void CleanupCache()
        {
            try
            {
                if (_ttsCacheDir == null) return;
                var dir = new DirectoryInfo(_ttsCacheDir);
                if (!dir.Exists) return;

                var files = dir.GetFiles("*.*")
                    .Where(f => string.Equals(f.Extension, ".wav", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(f.Extension, ".mp3", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                long totalSize = files.Sum(f => f.Length);
                long maxBytes = (long)TtsCacheMaxSizeMB * 1024 * 1024;

                if (totalSize <= maxBytes) return;

                // 按最后访问时间升序排列（最旧的排前面），逐个删除直到低于限制的 80%
                long targetSize = (long)(maxBytes * 0.8);
                var sorted = files.OrderBy(f => f.LastAccessTimeUtc).ToArray();
                foreach (var f in sorted)
                {
                    if (totalSize <= targetSize) break;
                    totalSize -= f.Length;
                    f.Delete();
                    Debug.WriteLine($"[SpeechService] Cache cleanup: deleted {f.Name}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Cache cleanup error: {ex.Message}");
            }
        }

        private void RequestCacheCleanup(string? newCachePath = null)
        {
            if (TtsCacheMaxSizeMB <= 0) return;

            if (!string.IsNullOrEmpty(newCachePath))
            {
                try
                {
                    if (File.Exists(newCachePath))
                        _cacheBytesSinceCleanup += new FileInfo(newCachePath).Length;
                }
                catch { }
            }

            bool firstRun = _lastCacheCleanupTime == DateTime.MinValue;
            bool enoughTimePassed = (DateTime.UtcNow - _lastCacheCleanupTime).TotalSeconds >= 60;
            bool enoughDataAdded = _cacheBytesSinceCleanup >= 16L * 1024 * 1024;
            if (!firstRun && !enoughTimePassed && !enoughDataAdded) return;

            CleanupCache();
            _lastCacheCleanupTime = DateTime.UtcNow;
            _cacheBytesSinceCleanup = 0;
        }

        /// <summary>清空全部 TTS 缓存</summary>
        public void ClearTtsCache()
        {
            try
            {
                if (_ttsCacheDir != null && Directory.Exists(_ttsCacheDir))
                {
                    foreach (var f in Directory.GetFiles(_ttsCacheDir, "*.*"))
                    {
                        var ext = Path.GetExtension(f);
                        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
                            File.Delete(f);
                    }
                    Debug.WriteLine("[SpeechService] TTS cache cleared");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechService] Clear cache error: {ex.Message}");
            }
        }

        /// <summary>
        /// 预生成语音缓存（不播放）。在后台线程中运行，适合收到订单数据时提前生成。
        /// </summary>
        public void PreGenerateCache(string text, bool isWarning = false)
        {
            if (!EnableAiTts) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            EnsureTtsCacheDir();

            // 预处理文本（与播放时保持一致）
            text = PreprocessTextForTts(text);
            PreGenerateCacheInternal(text, isWarning);
        }

        /// <summary>内部版本，text 已预处理，避免重复预处理</summary>
        private void PreGenerateCacheInternal(string text, bool isWarning)
        {
            if (!EnableAiTts) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            EnsureTtsCacheDir();

            string voiceKey = GetCurrentVoiceKey(isWarning);
            string extension = IsEdgeTtsEngine ? ".mp3" : ".wav";
            string cacheKey = GetCacheKey(text, AiTtsSpeed, voiceKey, AiTtsEngine);

            // 已有缓存则跳过
            string cachePath = Path.Combine(_ttsCacheDir!, cacheKey + extension);
            if (File.Exists(cachePath)) return;

            // 加入预生成队列，由专用单线程依次处理
            EnsurePreGenThreadStarted();
            try { _preGenQueue?.TryAdd((text, isWarning)); }
            catch (InvalidOperationException) { /* queue completed */ }
        }

        private void EnsurePreGenThreadStarted()
        {
            if (_preGenThread != null) return;
            lock (_kokoroLock)
            {
                if (_preGenThread != null) return;
                _preGenQueue = new BlockingCollection<(string text, bool isWarning)>();
                _preGenThread = new Thread(PreGenThreadLoop) { IsBackground = true, Name = "TtsPreGenThread" };
                _preGenThread.Start();
            }
        }

        private void PreGenThreadLoop()
        {
            try
            {
                foreach (var (text, isWarning) in _preGenQueue!.GetConsumingEnumerable())
                {
                    WaitWhilePaused();
                    if (_isDisposed) break;
                    try
                    {
                        string voiceKey = GetCurrentVoiceKey(isWarning);
                        string extension = IsEdgeTtsEngine ? ".mp3" : ".wav";
                        string cacheKey = GetCacheKey(text, AiTtsSpeed, voiceKey, AiTtsEngine);
                        string cachePath = Path.Combine(_ttsCacheDir!, cacheKey + extension);
                        if (File.Exists(cachePath)) continue;

                        if (IsEdgeTtsEngine)
                        {
                            GenerateEdgeTtsFile(text, isWarning, cachePath);
                        }
                        else
                        {
                            int sid = isWarning ? AiTtsWarningSpeakerId : AiTtsSpeakerId;
                            lock (_kokoroLock)
                            {
                                EnsureKokoroLoaded();
                                var tts = _kokoroTts;
                                if (tts == null) return;
                                _lastTtsUseTime = DateTime.Now;

                                Debug.WriteLine($"[SpeechService] PreGenerate Kokoro: sid={sid}, text=\"{text}\"");
                                var audio = tts.Generate(text, AiTtsSpeed, sid);
                                if (audio == null || audio.NumSamples <= 0)
                                {
                                    audio?.Dispose();
                                    continue;
                                }

                                try
                                {
                                    var wavData = BuildWav16(audio.Samples, audio.SampleRate);
                                    File.WriteAllBytes(cachePath, wavData);
                                    Debug.WriteLine($"[SpeechService] PreGenerate cached: {cacheKey}{extension} ({wavData.Length / 1024}KB)");
                                }
                                finally
                                {
                                    audio.Dispose();
                                }
                            }
                        }
                        RequestCacheCleanup(cachePath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SpeechService] PreGenerate error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        #endregion

        #region 电商文本预处理
        // 预编译正则表达式
        private static readonly Regex _reNumberRange = new(@"(\d+)\s*[-–—~～]\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex _reMultiply = new(@"(\d+)\s*[xX×]\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex _reSlash = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex _rePlusSign = new(@"(\d+)\s*\+\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex _reStar = new(@"\*+", RegexOptions.Compiled);
        private static readonly Regex _reMultiSpace = new(@"\s{2,}", RegexOptions.Compiled);
        // 匹配连续 10 个以上的中日韩字符（无标点/空格）
        private static readonly Regex _reLongCjk = new(@"[\u4e00-\u9fff\u3400-\u4dbf]{10,}", RegexOptions.Compiled);
        // 断句关键词（从配置读取，可在设置中编辑）
        private static HashSet<string> _breakWords = new(StringComparer.Ordinal);

        /// <summary>从配置更新断句关键词列表</summary>
        public void UpdateBreakWords(IEnumerable<string> words)
        {
            _breakWords = new HashSet<string>(words.Where(w => !string.IsNullOrWhiteSpace(w)), StringComparer.Ordinal);
        }

        /// <summary>
        /// 针对电商场景优化的文本预处理，用于 TTS 前的文本规范化。
        /// </summary>
        internal static string PreprocessTextForTts(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 1. 数字范围: "3-6个月" → "3到6个月", "100-200ml" → "100到200ml"
            text = _reNumberRange.Replace(text, "$1到$2");

            // 2. 乘号: "2x3" "2×3" → "2乘3"
            text = _reMultiply.Replace(text, "$1乘$2");

            // 3. 斜杠分隔: "S/M/L" → "S M L", "男/女" → "男 女"
            text = _reSlash.Replace(text, "$1或$2"); // 数字斜杠: "1/2" → "1或2"
            text = text.Replace("/", " "); // 其他斜杠作为分隔

            // 4. 加号: "2+1" → "2加1"
            text = _rePlusSign.Replace(text, "$1加$2");

            // 5. 星号遮罩（常见脱敏）: "张***" → "张"
            text = _reStar.Replace(text, "");

            // 6. 英文括号转中文括号（方便 TTS 停顿）
            text = text.Replace("(", "（").Replace(")", "）");

            // 7. 方括号、花括号替换为空格
            text = text.Replace("[", " ").Replace("]", " ").Replace("{", " ").Replace("}", " ");

            // 8. 连续标点去重: "！！！" → "！"
            text = DeduplicatePunctuation(text);

            // 9. 长连续中文文本自动断句（在关键词前插逗号，或按固定长度断）
            text = _reLongCjk.Replace(text, m => BreakLongCjk(m.Value));

            // 10. 清理多余空格
            text = _reMultiSpace.Replace(text, " ").Trim();

            return text;
        }

        private static string DeduplicatePunctuation(string text)
        {
            if (text.Length < 2) return text;
            var sb = new StringBuilder(text.Length);
            sb.Append(text[0]);
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                char prev = text[i - 1];
                // 跳过重复的中英文标点
                if (c == prev && (char.IsPunctuation(c) || c == '！' || c == '？' || c == '。' || c == '，'))
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 将长连续CJK字符串在关键词前插入逗号断句；如果两个断点之间仍然很长，则按固定长度再断。
        /// 例: "世喜奶嘴吸管嘴奶瓶配件原装手柄防尘盖吸管安抚奶嘴官方旗舰店"
        ///   → "世喜奶嘴吸管嘴奶瓶，配件，原装手柄，防尘盖吸管安抚奶嘴，官方旗舰店"
        /// </summary>
        private static string BreakLongCjk(string s)
        {
            if (s.Length < 10) return s;

            // 第一轮：在关键词前插入逗号
            var sb = new StringBuilder(s.Length + 16);
            int i = 0;
            int sinceLastBreak = 0;
            while (i < s.Length)
            {
                if (sinceLastBreak >= 4) // 断点之间至少保留4个字
                {
                    foreach (var kw in _breakWords)
                    {
                        if (i + kw.Length <= s.Length && s.AsSpan(i, kw.Length).SequenceEqual(kw.AsSpan()))
                        {
                            sb.Append('，');
                            sinceLastBreak = 0;
                            break;
                        }
                    }
                }
                sb.Append(s[i]);
                sinceLastBreak++;
                i++;
            }

            // 第二轮：如果仍有超长片段（>12字无标点），按固定长度断
            string result = sb.ToString();
            var parts = result.Split('，');
            bool needSecondPass = false;
            foreach (var p in parts)
            {
                if (p.Length > 12) { needSecondPass = true; break; }
            }

            if (!needSecondPass) return result;

            var sb2 = new StringBuilder(result.Length + 8);
            for (int pi = 0; pi < parts.Length; pi++)
            {
                if (pi > 0) sb2.Append('，');
                string part = parts[pi];
                if (part.Length > 12)
                {
                    // 每 8 个字断一次
                    for (int j = 0; j < part.Length; j++)
                    {
                        if (j > 0 && j % 8 == 0)
                            sb2.Append('，');
                        sb2.Append(part[j]);
                    }
                }
                else
                {
                    sb2.Append(part);
                }
            }
            return sb2.ToString();
        }
        #endregion

        /// <summary>将 float PCM [-1,1] 转为 16-bit mono WAV byte[]</summary>
        private static byte[] BuildWav16(float[] samples, int sampleRate)
        {
            int numSamples = samples.Length;
            int dataSize = numSamples * 2;
            int fileSize = 44 + dataSize;

            var wav = new byte[fileSize];
            // RIFF header
            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            BitConverter.GetBytes(fileSize - 8).CopyTo(wav, 4);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
            // fmt chunk
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            BitConverter.GetBytes(16).CopyTo(wav, 16);           // chunk size
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);     // PCM
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);     // mono
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);   // sample rate
            BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28); // byte rate
            BitConverter.GetBytes((short)2).CopyTo(wav, 32);     // block align
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);    // bits per sample
            // data chunk
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
            BitConverter.GetBytes(dataSize).CopyTo(wav, 40);

            for (int i = 0; i < numSamples; i++)
            {
                float s = samples[i];
                if (s > 1.0f) s = 1.0f;
                else if (s < -1.0f) s = -1.0f;
                short val = (short)(s * 32767);
                BitConverter.GetBytes(val).CopyTo(wav, 44 + i * 2);
            }
            return wav;
        }

        private void PlayWarningAlertToneBlocking()
        {
            try
            {
                PlayWavBlocking(GetWarningAlertToneWav());
            }
            catch (Exception ex)
            {
                // Warning tone failure must not block the following voice prompt.
                Debug.WriteLine($"[SpeechService] Warning tone playback error: {ex.Message}");
            }
        }

        private static byte[] GetWarningAlertToneWav()
        {
            return _warningAlertToneWav ??= BuildWarningAlertToneWav();
        }

        private void PlayRemarkToneBlocking()
        {
            try
            {
                PlayWavBlocking(GetRemarkToneWav());
            }
            catch (Exception ex)
            {
                // Remark tone failure must not block the following voice prompt.
                Debug.WriteLine($"[SpeechService] Remark tone playback error: {ex.Message}");
            }
        }

        private static byte[] GetRemarkToneWav()
        {
            return _remarkToneWav ??= BuildRemarkToneWav();
        }

        private static byte[] BuildWarningAlertToneWav()
        {
            const int sampleRate = 22050;
            const int toneMs = 90;
            const int gapMs = 35;
            const float volume = 0.72f;

            int[] tones = [880, 660, 880, 660];
            int toneSamples = sampleRate * toneMs / 1000;
            int gapSamples = sampleRate * gapMs / 1000;
            var samples = new float[tones.Length * toneSamples + (tones.Length - 1) * gapSamples];
            int offset = 0;

            foreach (int frequency in tones)
            {
                for (int i = 0; i < toneSamples; i++)
                {
                    double t = i / (double)sampleRate;
                    double envelope = BuildToneEnvelope(i, toneSamples);
                    samples[offset + i] = (float)(Math.Sin(2.0 * Math.PI * frequency * t) * volume * envelope);
                }

                offset += toneSamples;
                if (offset < samples.Length)
                    offset += gapSamples;
            }

            return BuildWav16(samples, sampleRate);
        }

        private static byte[] BuildRemarkToneWav()
        {
            const int sampleRate = 22050;
            const int toneMs = 120;
            const int gapMs = 45;
            const float volume = 0.50f;

            int[] tones = [660, 880];
            int toneSamples = sampleRate * toneMs / 1000;
            int gapSamples = sampleRate * gapMs / 1000;
            var samples = new float[tones.Length * toneSamples + (tones.Length - 1) * gapSamples];
            int offset = 0;

            foreach (int frequency in tones)
            {
                for (int i = 0; i < toneSamples; i++)
                {
                    double t = i / (double)sampleRate;
                    double envelope = BuildToneEnvelope(i, toneSamples);
                    samples[offset + i] = (float)(Math.Sin(2.0 * Math.PI * frequency * t) * volume * envelope);
                }

                offset += toneSamples;
                if (offset < samples.Length)
                    offset += gapSamples;
            }

            return BuildWav16(samples, sampleRate);
        }

        private static double BuildToneEnvelope(int sampleIndex, int totalSamples)
        {
            int edgeSamples = Math.Max(1, totalSamples / 10);
            if (sampleIndex < edgeSamples)
                return sampleIndex / (double)edgeSamples;
            if (sampleIndex >= totalSamples - edgeSamples)
                return (totalSamples - sampleIndex - 1) / (double)edgeSamples;
            return 1.0;
        }

        private static byte[]? _warningAlertToneWav;
        private static byte[]? _remarkToneWav;

        private void PlayWavBlocking(byte[] wavData)
        {
            if (wavData.Length < 44) return;
            int fmtSize = BitConverter.ToInt32(wavData, 16);
            int fmtEnd = 20 + fmtSize;
            int dataOffset = fmtEnd;
            int dataSize = 0;
            while (dataOffset + 8 <= wavData.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(wavData, dataOffset, 4);
                int chunkSize = BitConverter.ToInt32(wavData, dataOffset + 4);
                if (chunkId == "data")
                {
                    dataOffset += 8;
                    dataSize = Math.Min(chunkSize, wavData.Length - dataOffset);
                    break;
                }
                dataOffset += 8 + (chunkSize % 2 == 0 ? chunkSize : chunkSize + 1);
            }
            if (dataSize <= 0) return;

            byte[] wfx = new byte[Math.Max(fmtSize, 18)];
            Array.Copy(wavData, 20, wfx, 0, Math.Min(fmtSize, wfx.Length));

            IntPtr hwo = IntPtr.Zero;
            GCHandle dataHandle = default;
            try
            {
                int mmResult = waveOutOpen(out hwo, (uint)WAVE_MAPPER, wfx, null, IntPtr.Zero, CALLBACK_NULL);
                if (mmResult != 0) return;

                dataHandle = GCHandle.Alloc(wavData, GCHandleType.Pinned);
                var header = new WaveHeader
                {
                    lpData = dataHandle.AddrOfPinnedObject() + dataOffset,
                    dwBufferLength = (uint)dataSize
                };

                waveOutPrepareHeader(hwo, ref header, Marshal.SizeOf<WaveHeader>());
                waveOutWrite(hwo, ref header, Marshal.SizeOf<WaveHeader>());

                while ((header.dwFlags & WHDR_DONE) == 0 && !_speechCancelRequested && !_isDisposed)
                {
                    Thread.Sleep(20);
                }

                if (_speechCancelRequested || _isDisposed)
                {
                    waveOutReset(hwo);
                }

                waveOutUnprepareHeader(hwo, ref header, Marshal.SizeOf<WaveHeader>());
            }
            finally
            {
                if (hwo != IntPtr.Zero) waveOutClose(hwo);
                if (dataHandle.IsAllocated) dataHandle.Free();
            }
        }

        public void Stop()
        {
            _speechCancelRequested = true;
            lock (_filePlaybackLock)
            {
                try { _currentFileWaveOut?.Stop(); } catch { }
            }
            while (_speechQueue != null && _speechQueue.TryTake(out _)) { }
        }

        public void Speak(string text, bool cancelPrevious = true)
        {
            if (!EnableSoundPrompt) return;
            if (cancelPrevious) Stop();
            EnqueueSpeechRequest(new SpeechRequest { Text = text, IsWarning = false, RepeatCount = 1 });
        }

        public void SpeakWithRemarkTone(string text, bool cancelPrevious = true)
        {
            if (!EnableSoundPrompt) return;
            if (cancelPrevious) Stop();
            EnqueueSpeechRequest(new SpeechRequest { Text = text, IsWarning = false, RepeatCount = 1, PlayRemarkTone = true });
        }

        public void Preview(string text)
        {
            if (!EnableSoundPrompt) return;
            Stop();
            EnqueueSpeechRequest(new SpeechRequest
            {
                Text = text,
                IsWarning = false,
                RepeatCount = 1,
                PreferImmediateAiGeneration = true
            });
        }

        public void SpeakWarning(string text, int repeatCount = 1, bool cancelPrevious = true)
        {
            if (!EnableSoundPrompt) return;
            if (cancelPrevious) Stop();
            EnqueueSpeechRequest(new SpeechRequest { Text = text, IsWarning = true, RepeatCount = repeatCount });
        }

        #region winmm.dll
        private delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, byte[] pwfx, WaveOutProc? dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, ref WaveHeader pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, ref WaveHeader pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, ref WaveHeader pwh, int cbwh);
        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);
        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_NULL = 0x00000000;
        private const int WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }
        #endregion

        /// <summary>如果模型已卸载则重新加载</summary>
        private void EnsureKokoroLoaded()
        {
            if (_kokoroTts != null || _isDisposed || !EnableAiTts || IsEdgeTtsEngine) return;
            lock (_kokoroLock)
            {
                if (_kokoroTts != null) return;
                Debug.WriteLine("[SpeechService] Kokoro 模型按需重新加载...");
                InitAiTts();
            }
        }

        /// <summary>启动空闲卸载定时器</summary>
        private void StartIdleUnloadTimer()
        {
            _idleUnloadTimer?.Dispose();
            if (AiTtsIdleUnloadMinutes <= 0) return;
            _idleUnloadTimer = new Timer(_ => CheckIdleUnload(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        private void CheckIdleUnload()
        {
            if (_isDisposed || _kokoroTts == null || AiTtsIdleUnloadMinutes <= 0) return;
            if ((DateTime.Now - _lastTtsUseTime).TotalMinutes < AiTtsIdleUnloadMinutes) return;

            lock (_kokoroLock)
            {
                if (_kokoroTts == null) return;
                if ((DateTime.Now - _lastTtsUseTime).TotalMinutes < AiTtsIdleUnloadMinutes) return;
                Debug.WriteLine($"[SpeechService] Kokoro 模型空闲 {AiTtsIdleUnloadMinutes} 分钟，卸载释放内存");
                _kokoroTts.Dispose();
                _kokoroTts = null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            _isDisposed = true;
            Stop();
            _speechProcessingGate.Set();
            _idleUnloadTimer?.Dispose();
            try { _preGenQueue?.CompleteAdding(); } catch { }
            bool preGenStopped = TryJoinThread(_preGenThread, 2000);
            try { _speechQueue?.CompleteAdding(); } catch { }
            bool speechStopped = TryJoinThread(_speechThread, 2000);

            if (preGenStopped && speechStopped)
            {
                CleanupResources();
                return;
            }

            Debug.WriteLine("[SpeechService] 工作线程仍在退出，延后释放共享资源");
            _ = Task.Run(() =>
            {
                try
                {
                    _preGenThread?.Join();
                    _speechThread?.Join();
                    CleanupResources();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SpeechService] 延后释放资源失败: {ex.Message}");
                }
            });
        }

        private static bool TryJoinThread(Thread? thread, int timeoutMs)
        {
            if (thread == null || !thread.IsAlive) return true;
            try { return thread.Join(timeoutMs); }
            catch { return !thread.IsAlive; }
        }

        private void CleanupResources()
        {
            if (Interlocked.Exchange(ref _resourceCleanupStarted, 1) != 0) return;

            _ttsNormal?.Dispose();
            _ttsNormal = null;
            _ttsWarning?.Dispose();
            _ttsWarning = null;
            lock (_kokoroLock) { _kokoroTts?.Dispose(); _kokoroTts = null; }
            _speechQueue?.Dispose();
            _preGenQueue?.Dispose();
            _speechProcessingGate.Dispose();
        }
    }
}
