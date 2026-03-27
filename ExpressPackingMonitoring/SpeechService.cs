using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Media.SpeechSynthesis;

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
        private BlockingCollection<SpeechRequest> _speechQueue;
        private Thread _speechThread;
        private volatile bool _speechCancelRequested;
        private bool _isDisposed;

        public bool EnableSoundPrompt { get; set; } = true;

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
                        var synth = req.IsWarning ? _ttsWarning : _ttsNormal;
                        if (synth == null) continue;

                        string fullText = req.RepeatCount > 1
                            ? string.Join("，", Enumerable.Repeat(req.Text, req.RepeatCount))
                            : req.Text;

                        var result = synth.SynthesizeTextToStreamAsync(fullText).AsTask().GetAwaiter().GetResult();
                        using var ms = new MemoryStream();
                        result.AsStreamForRead().CopyTo(ms);
                        var wavData = ms.ToArray();

                        if (_speechCancelRequested || wavData.Length < 44) continue;

                        PlayWavBlocking(wavData);
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
            _speechQueue?.Dispose();
        }
    }
}
