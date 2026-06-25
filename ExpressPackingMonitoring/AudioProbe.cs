using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using ExpressPackingMonitoring.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ExpressPackingMonitoring
{
    internal static class AudioProbe
    {
        public static bool TryHandleCommandLine(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (!args.Any(a => string.Equals(a, "--audio-probe", StringComparison.OrdinalIgnoreCase)))
                return false;

            int seconds = ParseSeconds(args);
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.log");
            try
            {
                var result = Run(seconds, logPath);
                exitCode = result ? 0 : 2;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, $"Audio probe exception: {ex}\r\n", Encoding.UTF8);
                exitCode = 1;
            }
            return true;
        }

        private static int ParseSeconds(string[] args)
        {
            int index = Array.FindIndex(args, a => string.Equals(a, "--audio-probe", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int seconds))
                return Math.Clamp(seconds, 3, 120);
            return 15;
        }

        private static bool Run(int seconds, string logPath)
        {
            var config = LoadConfig();
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            using var device = ResolveAudioEndpoint(config, devices);
            string wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.wav");
            string mkvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.mkv");
            string mp4Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.mp4");
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            try { if (File.Exists(mkvPath)) File.Delete(mkvPath); } catch { }
            try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }

            var log = new StringBuilder();
            log.AppendLine($"Audio probe started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"DurationSeconds={seconds}");
            log.AppendLine($"ConfiguredName={config.AudioDeviceName}");
            log.AppendLine($"ConfiguredEndpoint={config.AudioDeviceMoniker}");
            log.AppendLine($"SelectedDevice={device.FriendlyName}");
            log.AppendLine($"SelectedEndpoint={device.ID}");

            int packets = 0;
            long rawBytes = 0;
            long bytes = 0;
            short peak = 0;
            int gapCount = 0;
            double maxGapMs = 0;
            int selectedChannel = -1;
            double resamplePosition = 0;
            short previousSourceSample = 0;
            bool hasPreviousSourceSample = false;
            bool writeFailed = false;
            bool queueFull = false;
            bool writeCompleted = false;
            string? writeError = null;
            DateTime lastPacketAt = DateTime.MinValue;
            using var capture = new WasapiCapture(device, true, 100)
            {
                ShareMode = AudioClientShareMode.Shared
            };
            var writerFormat = MainViewModel.CreatePcm16WaveFormat(capture.WaveFormat);
            var writer = new WaveFileWriter(wavPath, writerFormat);
            using var writeQueue = new BlockingCollection<byte[]>(boundedCapacity: 150);
            var writeTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    foreach (var chunk in writeQueue.GetConsumingEnumerable())
                        writer.Write(chunk, 0, chunk.Length);
                    writer.Flush();
                }
                catch (Exception ex)
                {
                    writeFailed = true;
                    writeError = ex.Message;
                }
                finally
                {
                    try { writer.Dispose(); } catch { }
                }
            });

            log.AppendLine($"SourceFormat={capture.WaveFormat}");
            log.AppendLine($"WavFormat={writerFormat}");
            log.AppendLine("WasapiEventSync=true");
            log.AppendLine("BufferMs=100");
            log.AppendLine($"WavPath={wavPath}");

            capture.DataAvailable += (_, e) =>
            {
                var now = DateTime.Now;
                if (lastPacketAt != DateTime.MinValue)
                {
                    double gapMs = (now - lastPacketAt).TotalMilliseconds;
                    if (gapMs > 750)
                    {
                        gapCount++;
                        if (gapMs > maxGapMs) maxGapMs = gapMs;
                    }
                }

                lastPacketAt = now;
                packets++;
                rawBytes += e.BytesRecorded;
                byte[]? pcmBytes = MainViewModel.ConvertCaptureBufferToPcm16(
                    e.Buffer,
                    e.BytesRecorded,
                    capture.WaveFormat,
                    writerFormat,
                    ref selectedChannel,
                    ref resamplePosition,
                    ref previousSourceSample,
                    ref hasPreviousSourceSample);
                if (pcmBytes == null || pcmBytes.Length == 0)
                    return;

                if (!writeQueue.TryAdd(pcmBytes))
                {
                    queueFull = true;
                    writeFailed = true;
                    return;
                }
                bytes += pcmBytes.Length;
                if (MainViewModel.TryGetAudioPeak(pcmBytes, pcmBytes.Length, writerFormat, out short packetPeak) && packetPeak > peak)
                    peak = packetPeak;
            };

            Exception? stoppedException = null;
            capture.RecordingStopped += (_, e) => stoppedException = e.Exception;
            string? ffmpegPath = FindFFmpeg();

            capture.StartRecording();
            var videoTask = System.Threading.Tasks.Task.Run<(bool Exited, int ExitCode, string Stderr)>(() =>
            {
                return string.IsNullOrEmpty(ffmpegPath)
                    ? (false, -1, "ffmpeg.exe not found.")
                    : WriteSyntheticMkv(ffmpegPath, mkvPath, seconds);
            });
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            capture.StopRecording();
            var videoInfo = videoTask.GetAwaiter().GetResult();
            byte[]? tailBytes = MainViewModel.FlushResamplerTail(previousSourceSample, hasPreviousSourceSample, ref resamplePosition);
            if (tailBytes != null && tailBytes.Length > 0)
            {
                if (!writeQueue.TryAdd(tailBytes))
                {
                    queueFull = true;
                    writeFailed = true;
                }
                else
                {
                    bytes += tailBytes.Length;
                }
            }
            writeQueue.CompleteAdding();
            writeCompleted = writeTask.Wait(5000);
            if (!writeCompleted)
                writeFailed = true;

            var wavInfo = ReadWavInfo(wavPath);
            var wavTimeline = ReadWavTimeline(wavPath);
            var mp4Info = ProbeMp4Mux(mkvPath, wavPath, mp4Path, seconds, config.AudioSyncOffsetMs);
            bool ok = stoppedException == null
                && videoInfo.Exited
                && videoInfo.ExitCode == 0
                && !writeFailed
                && writeCompleted
                && packets > 0
                && bytes > 0
                && gapCount == 0
                && wavInfo.Valid
                && wavInfo.DurationSeconds >= seconds * 0.8
                && wavTimeline.Valid
                && !LooksLikeShortPulseThenSilence(wavInfo.DurationSeconds, wavTimeline.ActiveWindowCount, wavTimeline.MaxConsecutiveActiveWindows, wavTimeline.LastActiveSecond)
                && mp4Info.Valid;
            log.AppendLine($"Packets={packets}");
            log.AppendLine($"RawBytes={rawBytes}");
            log.AppendLine($"Bytes={bytes}");
            log.AppendLine($"Peak={peak}");
            log.AppendLine($"SelectedChannel={selectedChannel}");
            log.AppendLine($"GapCount={gapCount}");
            log.AppendLine($"MaxGapMs={maxGapMs:F0}");
            log.AppendLine($"WavValid={wavInfo.Valid}");
            log.AppendLine($"WavWriterCompleted={writeCompleted}");
            log.AppendLine($"WavWriterFailed={writeFailed}");
            log.AppendLine($"WavWriterQueueFull={queueFull}");
            log.AppendLine($"WavWriterError={writeError ?? "(none)"}");
            log.AppendLine($"WavBytes={wavInfo.FileBytes}");
            log.AppendLine($"WavDurationSeconds={wavInfo.DurationSeconds:F2}");
            log.AppendLine($"WavError={wavInfo.Error ?? "(none)"}");
            log.AppendLine($"WavTimelineValid={wavTimeline.Valid}");
            log.AppendLine($"WavFirstActiveSecond={wavTimeline.FirstActiveSecond:F1}");
            log.AppendLine($"WavLastActiveSecond={wavTimeline.LastActiveSecond:F1}");
            log.AppendLine($"WavActiveWindows={wavTimeline.ActiveWindowCount}");
            log.AppendLine($"WavMaxConsecutiveActiveWindows={wavTimeline.MaxConsecutiveActiveWindows}");
            log.AppendLine($"WavTimelineError={wavTimeline.Error ?? "(none)"}");
            log.AppendLine($"MkvPath={mkvPath}");
            log.AppendLine($"MkvBytes={(File.Exists(mkvPath) ? new FileInfo(mkvPath).Length : 0)}");
            log.AppendLine($"MkvVideoExited={videoInfo.Exited}");
            log.AppendLine($"MkvVideoExitCode={videoInfo.ExitCode}");
            log.AppendLine($"MkvVideoError={(videoInfo.Exited && videoInfo.ExitCode == 0 ? "(none)" : TrimForLog(videoInfo.Stderr))}");
            log.AppendLine($"Mp4Path={mp4Path}");
            log.AppendLine($"Mp4Valid={mp4Info.Valid}");
            log.AppendLine($"Mp4Bytes={mp4Info.FileBytes}");
            log.AppendLine($"Mp4AudioDecodeOk={mp4Info.AudioDecodeOk}");
            log.AppendLine($"Mp4Error={mp4Info.Error ?? "(none)"}");
            log.AppendLine($"StoppedException={stoppedException?.Message ?? "(none)"}");
            log.AppendLine($"Result={(ok ? "OK" : "FAILED")}");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return ok;
        }

        private static (bool Valid, long FileBytes, bool AudioDecodeOk, string? Error) ProbeMp4Mux(string mkvPath, string wavPath, string mp4Path, int seconds, int audioSyncOffsetMs)
        {
            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
                return (false, 0, false, "ffmpeg.exe not found.");

            if (!File.Exists(mkvPath) || new FileInfo(mkvPath).Length <= 0)
                return (false, 0, false, "Video MKV was not generated.");

            string muxArgs = MainViewModel.BuildMkvToMp4Args(mkvPath, wavPath, mp4Path, audioSyncOffsetMs);
            var mux = RunProcess(ffmpegPath, muxArgs, Math.Max(15000, seconds * 3000));
            if (!mux.Exited || mux.ExitCode != 0 || !File.Exists(mp4Path) || new FileInfo(mp4Path).Length <= 0)
                return (false, File.Exists(mp4Path) ? new FileInfo(mp4Path).Length : 0, false, $"Mux failed: exited={mux.Exited}, exitCode={mux.ExitCode}, stderr={TrimForLog(mux.Stderr)}");

            string decodeArgs = $"-v error -i \"{mp4Path}\" -map 0:a:0 -f null NUL";
            var decode = RunProcess(ffmpegPath, decodeArgs, Math.Max(15000, seconds * 3000));
            bool decodeOk = decode.Exited && decode.ExitCode == 0;
            string? error = decodeOk ? null : $"Audio decode failed: exited={decode.Exited}, exitCode={decode.ExitCode}, stderr={TrimForLog(decode.Stderr)}";
            return (decodeOk, new FileInfo(mp4Path).Length, decodeOk, error);
        }

        private static (bool Exited, int ExitCode, string Stderr) WriteSyntheticMkv(string ffmpegPath, string mkvPath, int seconds)
        {
            const int width = 320;
            const int height = 180;
            const int fps = 10;
            string args = MainViewModel.BuildFFmpegArgs(width, height, fps, mkvPath, "libx264", false, 35);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, -1, "Process failed to start.");

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            byte[] frame = new byte[width * height * 3];
            int totalFrames = seconds * fps;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < totalFrames; i++)
                {
                    FillBgrFrame(frame, width, height, i);
                    process.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                    double targetMs = (i + 1) * 1000.0 / fps;
                    int sleepMs = (int)(targetMs - stopwatch.Elapsed.TotalMilliseconds);
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                }
                process.StandardInput.Close();
            }
            catch (Exception ex)
            {
                try { process.Kill(); } catch { }
                return (false, -1, $"Pipe write failed: {ex.Message}");
            }

            bool exited = process.WaitForExit(Math.Max(15000, seconds * 3000));
            if (!exited)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(3000); } catch { }
            }

            string stderr = string.Empty;
            try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
            try { _ = stdoutTask.GetAwaiter().GetResult(); } catch { }
            return (exited, exited ? process.ExitCode : -1, stderr);
        }

        private static void FillBgrFrame(byte[] frame, int width, int height, int frameIndex)
        {
            int offset = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    frame[offset++] = (byte)((x + frameIndex * 3) % 256);
                    frame[offset++] = (byte)((y + frameIndex * 5) % 256);
                    frame[offset++] = (byte)((x + y + frameIndex * 7) % 256);
                }
            }
        }

        private static string? FindFFmpeg()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            string projectLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ffmpeg.exe");
            string fullProjectLocal = Path.GetFullPath(projectLocal);
            if (File.Exists(fullProjectLocal)) return fullProjectLocal;

            return null;
        }

        private static (bool Exited, int ExitCode, string Stderr) RunProcess(string fileName, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, -1, "Process failed to start.");

            string stderr = string.Empty;
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(3000); } catch { }
            }

            try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
            try { _ = stdoutTask.GetAwaiter().GetResult(); } catch { }
            return (exited, exited ? process.ExitCode : -1, stderr);
        }

        private static string TrimForLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }

        private static (bool Valid, long FileBytes, double DurationSeconds, string? Error) ReadWavInfo(string wavPath)
        {
            try
            {
                if (!File.Exists(wavPath))
                    return (false, 0, 0, "File not found.");
                using var reader = new WaveFileReader(wavPath);
                return (reader.Length > 0, new FileInfo(wavPath).Length, reader.TotalTime.TotalSeconds, null);
            }
            catch (Exception ex)
            {
                return (false, File.Exists(wavPath) ? new FileInfo(wavPath).Length : 0, 0, ex.Message);
            }
        }

        private static (bool Valid, double FirstActiveSecond, double LastActiveSecond, int ActiveWindowCount, int MaxConsecutiveActiveWindows, string? Error) ReadWavTimeline(string wavPath)
        {
            try
            {
                if (!File.Exists(wavPath))
                    return (false, -1, -1, 0, 0, "File not found.");

                using var reader = new WaveFileReader(wavPath);
                int bytesPerSecond = Math.Max(1, reader.WaveFormat.AverageBytesPerSecond);
                int blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
                int windowBytes = bytesPerSecond;
                windowBytes -= windowBytes % blockAlign;
                if (windowBytes <= 0) windowBytes = bytesPerSecond;

                byte[] buffer = new byte[windowBytes];
                int windowIndex = 0;
                double firstActiveSecond = -1;
                double lastActiveSecond = -1;
                int activeWindowCount = 0;
                int consecutiveActiveWindows = 0;
                int maxConsecutiveActiveWindows = 0;

                while (true)
                {
                    int totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        int read = reader.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                    if (totalRead <= 0) break;

                    double start = windowIndex;
                    double end = Math.Min(reader.TotalTime.TotalSeconds, start + (double)totalRead / bytesPerSecond);
                    if (MainViewModel.TryGetAudioPeak(buffer, totalRead, reader.WaveFormat, out short peak) && peak > 32)
                    {
                        if (firstActiveSecond < 0)
                            firstActiveSecond = start;
                        lastActiveSecond = end;
                        activeWindowCount++;
                        consecutiveActiveWindows++;
                        if (consecutiveActiveWindows > maxConsecutiveActiveWindows)
                            maxConsecutiveActiveWindows = consecutiveActiveWindows;
                    }
                    else
                    {
                        consecutiveActiveWindows = 0;
                    }
                    windowIndex++;
                }

                return (true, firstActiveSecond, lastActiveSecond, activeWindowCount, maxConsecutiveActiveWindows, null);
            }
            catch (Exception ex)
            {
                return (false, -1, -1, 0, 0, ex.Message);
            }
        }

        private static bool LooksLikeShortPulseThenSilence(double durationSeconds, int activeWindowCount, int maxConsecutiveActiveWindows, double lastActiveSecond)
        {
            if (durationSeconds < 30 || lastActiveSecond < 0)
                return false;

            double trailingSilentSeconds = durationSeconds - lastActiveSecond;
            return activeWindowCount > 0
                && activeWindowCount <= 4
                && maxConsecutiveActiveWindows <= 2
                && trailingSilentSeconds >= 5;
        }

        private static AppConfig LoadConfig()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (!File.Exists(configPath)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
        }

        private static MMDevice ResolveAudioEndpoint(AppConfig config, MMDeviceCollection devices)
        {
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("No active capture endpoint was found.");

            bool hasConfiguredEndpoint = false;
            if (!string.IsNullOrWhiteSpace(config.AudioDeviceMoniker))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.ID, config.AudioDeviceMoniker))
                        return device;
                }
            }

            if (!string.IsNullOrWhiteSpace(config.AudioDeviceName))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.FriendlyName, config.AudioDeviceName)
                        || AudioEndpointMatches(GetEndpointDisplayName(device), config.AudioDeviceName))
                        return device;
                }
            }

            if (hasConfiguredEndpoint)
                throw new InvalidOperationException("Configured microphone endpoint was not found.");

            using var enumerator = new MMDeviceEnumerator();
            try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
            catch { return devices[0]; }
        }

        private static string GetEndpointDisplayName(MMDevice device)
        {
            try { return device.DeviceFriendlyName; } catch { return device.FriendlyName; }
        }

        private static bool AudioEndpointMatches(string endpointName, string configuredName)
        {
            if (string.IsNullOrWhiteSpace(endpointName) || string.IsNullOrWhiteSpace(configuredName))
                return false;

            return endpointName.Equals(configuredName, StringComparison.OrdinalIgnoreCase)
                || endpointName.Contains(configuredName, StringComparison.OrdinalIgnoreCase)
                || configuredName.Contains(endpointName, StringComparison.OrdinalIgnoreCase);
        }

    }
}
