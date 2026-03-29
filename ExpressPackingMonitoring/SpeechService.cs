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
using Windows.Media.SpeechSynthesis;
using SherpaOnnx;

namespace ExpressPackingMonitoring.Services
{
    public class SpeechRequest
    {
        public string Text { get; set; } = string.Empty;
        public bool IsWarning { get; set; }
        public int RepeatCount { get; set; } = 1;
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
        private bool _isDisposed;
        private string? _ttsCacheDir;
        private DateTime _lastTtsUseTime = DateTime.MinValue;
        private Timer? _idleUnloadTimer;
        private BlockingCollection<(string text, bool isWarning)>? _preGenQueue;
        private Thread? _preGenThread;

        /// <summary>AI TTS 模型空闲多少分钟后自动卸载释放内存，0 = 不自动卸载</summary>
        public int AiTtsIdleUnloadMinutes { get; set; } = 10;

        /// <summary>TTS 缓存目录最大占用空间（MB），超出后按最久未访问清理，0 = 不限制</summary>
        public int TtsCacheMaxSizeMB { get; set; } = 500;

        public bool EnableSoundPrompt { get; set; } = true;
        public bool EnableAiTts { get; set; } = false;
        public int AiTtsSpeakerId { get; set; } = 51;
        public int AiTtsWarningSpeakerId { get; set; } = 50;
        public float AiTtsSpeed { get; set; } = 1.0f;

        /// <summary>推理提供者：cpu / directml / cuda。切换 GPU 需要对应的 onnxruntime DLL</summary>
        public string AiTtsProvider { get; set; } = "cpu";

        /// <summary>AI TTS 模型是否已成功加载</summary>
        public bool IsAiTtsAvailable => _kokoroTts != null;

        public SpeechService()
        {
            InitSpeechSynthesizer();
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
        public void InitAiTts(string modelDir = null)
        {
            try
            {
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

                config.Model.NumThreads = Math.Max(2, Environment.ProcessorCount / 2);
                config.Model.Provider = AiTtsProvider ?? "cpu";
                config.MaxNumSentences = 0; // 0 = 不限制句数，避免长文本被截断

                // 初始化磁盘缓存目录
                _ttsCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tts_cache");
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

                        if (EnableAiTts && _kokoroTts != null)
                        {
                            SpeakWithKokoro(fullText, req.IsWarning);
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

        private void SpeakWithWindowsTts(string text, bool isWarning)
        {
            var synth = isWarning ? _ttsWarning : _ttsNormal;
            if (synth == null) return;

            var result = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            result.AsStreamForRead().CopyTo(ms);
            var wavData = ms.ToArray();

            if (_speechCancelRequested || wavData.Length < 44) return;
            PlayWavBlocking(wavData);
        }

        private void SpeakWithKokoro(string text, bool isWarning)
        {
            int sid = isWarning ? AiTtsWarningSpeakerId : AiTtsSpeakerId;

            // 电商场景文本预处理
            text = PreprocessTextForTts(text);

            Debug.WriteLine($"[SpeechService] Kokoro: sid={sid}, speed={AiTtsSpeed}, text=\"{text}\"");

            // 1. 尝试从磁盘缓存读取（命中缓存不需要加载模型）
            string cacheKey = GetCacheKey(text, AiTtsSpeed, sid);
            byte[]? wavData = LoadFromCache(cacheKey);
            if (wavData != null)
            {
                Debug.WriteLine($"[SpeechService] Cache HIT: {cacheKey}");
                if (!_speechCancelRequested && !_isDisposed)
                    PlayWavBlocking(wavData);
                return;
            }

            // 2. 缓存未命中，确保模型已加载，然后生成音频（与预生成互斥，同一时刻只有一个 Generate）
            Debug.WriteLine($"[SpeechService] Cache MISS, generating...");
            lock (_kokoroLock)
            {
                EnsureKokoroLoaded();
                var tts = _kokoroTts;
                if (tts == null) return;
                _lastTtsUseTime = DateTime.Now;

                OfflineTtsGeneratedAudio? audio;
                try
                {
                    audio = tts.Generate(text, AiTtsSpeed, sid);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SpeechService] Kokoro Generate exception: {ex.Message}");
                    return;
                }

                if (audio == null || audio.NumSamples <= 0)
                {
                    Debug.WriteLine($"[SpeechService] Kokoro returned empty audio (NumSamples={audio?.NumSamples})");
                    audio?.Dispose();
                    return;
                }

                Debug.WriteLine($"[SpeechService] Kokoro generated {audio.NumSamples} samples, sampleRate={audio.SampleRate}, duration={audio.NumSamples / (double)audio.SampleRate:F2}s");

                try
                {
                    wavData = BuildWav16(audio.Samples, audio.SampleRate);
                    SaveToCache(cacheKey, wavData);
                }
                finally
                {
                    audio.Dispose();
                }
            }

            if (!_speechCancelRequested && !_isDisposed)
                PlayWavBlocking(wavData);
        }

        #region TTS 磁盘缓存
        private static string GetCacheKey(string text, float speed, int sid)
        {
            string raw = $"{text}|{speed:F2}|{sid}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
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

                if (TtsCacheMaxSizeMB > 0)
                    CleanupCache();
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

                var files = dir.GetFiles("*.wav");
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

        /// <summary>清空全部 TTS 缓存</summary>
        public void ClearTtsCache()
        {
            try
            {
                if (_ttsCacheDir != null && Directory.Exists(_ttsCacheDir))
                {
                    foreach (var f in Directory.GetFiles(_ttsCacheDir, "*.wav"))
                        File.Delete(f);
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
            if (!EnableAiTts || _ttsCacheDir == null) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            // 预处理文本（与播放时保持一致）
            text = PreprocessTextForTts(text);

            int sid = isWarning ? AiTtsWarningSpeakerId : AiTtsSpeakerId;
            string cacheKey = GetCacheKey(text, AiTtsSpeed, sid);

            // 已有缓存则跳过
            string cachePath = Path.Combine(_ttsCacheDir, cacheKey + ".wav");
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
                    if (_isDisposed) break;
                    try
                    {
                        int sid = isWarning ? AiTtsWarningSpeakerId : AiTtsSpeakerId;
                        string cacheKey = GetCacheKey(text, AiTtsSpeed, sid);
                        string cachePath = Path.Combine(_ttsCacheDir!, cacheKey + ".wav");
                        if (File.Exists(cachePath)) continue;

                        lock (_kokoroLock)
                        {
                            EnsureKokoroLoaded();
                            var tts = _kokoroTts;
                            if (tts == null) return;
                            _lastTtsUseTime = DateTime.Now;

                            Debug.WriteLine($"[SpeechService] PreGenerate: sid={sid}, text=\"{text}\"");
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
                                Debug.WriteLine($"[SpeechService] PreGenerate cached: {cacheKey}.wav ({wavData.Length / 1024}KB)");
                            }
                            finally
                            {
                                audio.Dispose();
                            }
                        }

                        if (TtsCacheMaxSizeMB > 0)
                            CleanupCache();
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
            while (_speechQueue != null && _speechQueue.TryTake(out _)) { }
        }

        public void Speak(string text, bool cancelPrevious = true)
        {
            if (!EnableSoundPrompt) return;
            var queue = _speechQueue;
            if (queue == null || queue.IsAddingCompleted) return;
            if (cancelPrevious) Stop();
            try { queue.Add(new SpeechRequest { Text = text, IsWarning = false, RepeatCount = 1 }); } catch { }
        }

        public void SpeakWarning(string text, int repeatCount = 1, bool cancelPrevious = true)
        {
            if (!EnableSoundPrompt) return;
            var queue = _speechQueue;
            if (queue == null || queue.IsAddingCompleted) return;
            if (cancelPrevious) Stop();
            try { queue.Add(new SpeechRequest { Text = text, IsWarning = true, RepeatCount = repeatCount }); } catch { }
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
            if (_kokoroTts != null || _isDisposed || !EnableAiTts) return;
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
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            _idleUnloadTimer?.Dispose();
            try { _preGenQueue?.CompleteAdding(); } catch { }
            try { _preGenThread?.Join(2000); } catch { }
            try { _speechQueue?.CompleteAdding(); } catch { }
            try { _speechThread?.Join(2000); } catch { }
            _ttsNormal?.Dispose();
            _ttsWarning?.Dispose();
            lock (_kokoroLock) { _kokoroTts?.Dispose(); _kokoroTts = null; }
            _speechQueue?.Dispose();
        }
    }
}
