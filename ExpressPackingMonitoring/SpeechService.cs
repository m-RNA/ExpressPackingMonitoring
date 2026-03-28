using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Media.SpeechSynthesis;
using SherpaOnnx;

namespace ExpressPackingMonitoring.Services
{
    public class SpeechRequest
    {
        public string Text { get; set; }
        public bool IsWarning { get; set; }
        public int RepeatCount { get; set; } = 1;
    }

    public class SpeechService : IDisposable
    {
        private SpeechSynthesizer _ttsNormal;
        private SpeechSynthesizer _ttsWarning;
        private OfflineTts _kokoroTts;
        private BlockingCollection<SpeechRequest> _speechQueue;
        private Thread _speechThread;
        private volatile bool _speechCancelRequested;
        private bool _isDisposed;

        public bool EnableSoundPrompt { get; set; } = true;
        public bool EnableAiTts { get; set; } = false;
        public int AiTtsSpeakerId { get; set; } = 51;
        public int AiTtsWarningSpeakerId { get; set; } = 50;
        public float AiTtsSpeed { get; set; } = 1.0f;

        /// <summary>AI TTS 模型是否已成功加载</summary>
        public bool IsAiTtsAvailable => _kokoroTts != null;

        public SpeechService()
        {
            InitSpeechSynthesizer();
        }

        private void InitSpeechSynthesizer()
        {
            try
            {
                var voices = SpeechSynthesizer.AllVoices;
                var femaleZh = voices.FirstOrDefault(v => v.Gender == VoiceGender.Female && v.Language == "zh-CN");
                var maleZh = voices.FirstOrDefault(v => v.Gender == VoiceGender.Male && v.Language == "zh-CN");
                var anyZh = femaleZh ?? maleZh ?? voices.FirstOrDefault(v => v.Language.StartsWith("zh"));

                _ttsNormal = new SpeechSynthesizer();
                if (femaleZh != null) _ttsNormal.Voice = femaleZh;
                else if (anyZh != null) _ttsNormal.Voice = anyZh;

                _ttsWarning = new SpeechSynthesizer();
                if (maleZh != null) _ttsWarning.Voice = maleZh;
                else if (anyZh != null) _ttsWarning.Voice = anyZh;

                _speechQueue = new BlockingCollection<SpeechRequest>();
                _speechThread = new Thread(SpeechThreadLoop) { IsBackground = true, Name = "SpeechThread" };
                _speechThread.Start();
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

                config.Model.NumThreads = 2;
                config.Model.Provider = "cpu";
                config.MaxNumSentences = 0; // 0 = 不限制句数，避免长文本被截断

                _kokoroTts = new OfflineTts(config);
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
            // 使用回调来支持打断：生成过程中检查取消标志
            var audio = _kokoroTts.GenerateWithCallbackProgress(text, AiTtsSpeed, sid,
                (IntPtr samples, int n, float progress) =>
                {
                    // 返回 0 继续生成，返回 1 中止
                    return (_speechCancelRequested || _isDisposed) ? 1 : 0;
                });

            if (_speechCancelRequested || _isDisposed || audio == null || audio.NumSamples <= 0)
            {
                audio?.Dispose();
                return;
            }

            try
            {
                // 将 float[] PCM 转成 16-bit PCM WAV 送入 waveOut 播放
                var samples = audio.Samples;
                int sampleRate = audio.SampleRate;
                var wavData = BuildWav16(samples, sampleRate);

                if (!_speechCancelRequested && !_isDisposed)
                    PlayWavBlocking(wavData);
            }
            finally
            {
                audio.Dispose();
            }
        }

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
                dataOffset += 8 + chunkSize;
            }
            if (dataSize <= 0) return;

            byte[] wfx = new byte[Math.Max(fmtSize, 18)];
            Array.Copy(wavData, 20, wfx, 0, Math.Min(fmtSize, wfx.Length));

            IntPtr hwo = IntPtr.Zero;
            GCHandle dataHandle = default;
            try
            {
                int mmResult = waveOutOpen(out hwo, WAVE_MAPPER, wfx, null, IntPtr.Zero, CALLBACK_NULL);
                if (mmResult != 0) return;

                dataHandle = GCHandle.Alloc(wavData, GCHandleType.Pinned);
                var header = new WaveHeader
                {
                    lpData = dataHandle.AddrOfPinnedObject() + dataOffset,
                    dwBufferLength = dataSize
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
            if (!EnableSoundPrompt || _speechQueue == null || _speechQueue.IsAddingCompleted) return;
            if (cancelPrevious) Stop();
            try { _speechQueue.Add(new SpeechRequest { Text = text, IsWarning = false, RepeatCount = 1 }); } catch { }
        }

        public void SpeakWarning(string text, int repeatCount = 1, bool cancelPrevious = true)
        {
            if (!EnableSoundPrompt || _speechQueue == null || _speechQueue.IsAddingCompleted) return;
            if (cancelPrevious) Stop();
            try { _speechQueue.Add(new SpeechRequest { Text = text, IsWarning = true, RepeatCount = repeatCount }); } catch { }
        }

        #region winmm.dll
        private delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, byte[] pwfx, WaveOutProc dwCallback, IntPtr dwInstance, uint fdwOpen);
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
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }
        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            try { _speechQueue?.CompleteAdding(); } catch { }
            try { _speechThread?.Join(2000); } catch { }
            _ttsNormal?.Dispose();
            _ttsWarning?.Dispose();
            _kokoroTts?.Dispose();
            _speechQueue?.Dispose();
        }
    }
}
