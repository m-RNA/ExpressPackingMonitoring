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
            if (args.Any(a => string.Equals(a, "--audio-check", StringComparison.OrdinalIgnoreCase)))
            {
                exitCode = RunAudioCheck(args) ? 0 : 2;
                return true;
            }

            if (!args.Any(a => string.Equals(a, "--audio-probe", StringComparison.OrdinalIgnoreCase)))
                return false;

            int seconds = ParseSeconds(args);
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.log");
            try
            {
                int repeat = ParseRepeat(args);
                var result = repeat <= 1
                    ? Run(seconds, logPath, "audio_probe", args)
                    : RunRepeated(seconds, repeat, logPath, args);
                exitCode = result ? 0 : 2;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, $"Audio probe exception: {ex}\r\n", Encoding.UTF8);
                exitCode = 1;
            }
            return true;
        }

        private static bool RunAudioCheck(string[] args)
        {
            int index = Array.FindIndex(args, a => string.Equals(a, "--audio-check", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing file path after --audio-check.");
                return false;
            }

            string inputPath = args[index + 1];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"File not found: {inputPath}");
                return false;
            }

            string wavPath = inputPath;
            bool deleteWav = false;
            try
            {
                if (!inputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    string? ffmpegPath = FindFFmpeg();
                    if (string.IsNullOrEmpty(ffmpegPath))
                    {
                        Console.Error.WriteLine("FFmpeg not found.");
                        return false;
                    }

                    wavPath = Path.Combine(Path.GetTempPath(), $"audio_check_{Guid.NewGuid():N}.wav");
                    deleteWav = true;
                    var decode = RunProcess(ffmpegPath, $"-y -v error -i \"{inputPath}\" -map 0:a:0 -ac 1 -ar 48000 -sample_fmt s16 \"{wavPath}\"", 120000);
                    if (!decode.Exited || decode.ExitCode != 0 || !File.Exists(wavPath))
                    {
                        Console.Error.WriteLine($"Audio decode failed: exited={decode.Exited}, exitCode={decode.ExitCode}, stderr={TrimForLog(decode.Stderr)}");
                        return false;
                    }
                }

                var info = ReadWavInfo(wavPath);
                var timeline = ReadWavTimeline(wavPath);
                string reason = string.Empty;
                bool timelineUsable = MainViewModel.IsAudioTimelineUsable(
                        info.DurationSeconds,
                        timeline.FirstActiveSecond,
                        timeline.LastActiveSecond,
                        timeline.ActiveWindowCount,
                        timeline.MaxConsecutiveActiveWindows,
                        out reason);
                bool usable = info.Valid && timeline.Valid && timelineUsable;

                Console.WriteLine($"File={inputPath}");
                Console.WriteLine($"DurationSeconds={info.DurationSeconds:F2}");
                Console.WriteLine($"FirstActiveSecond={timeline.FirstActiveSecond:F1}");
                Console.WriteLine($"LastActiveSecond={timeline.LastActiveSecond:F1}");
                Console.WriteLine($"ActiveWindows={timeline.ActiveWindowCount}");
                Console.WriteLine($"MaxConsecutiveActiveWindows={timeline.MaxConsecutiveActiveWindows}");
                Console.WriteLine($"Usable={usable}");
                Console.WriteLine($"Reason={(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason)}");
                return usable;
            }
            finally
            {
                if (deleteWav)
                {
                    try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                }
            }
        }

        private static int ParseRepeat(string[] args)
        {
            int index = Array.FindIndex(args, a => string.Equals(a, "--repeat", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int repeat))
                return Math.Clamp(repeat, 1, 20);
            return 1;
        }

        private static int ParseSeconds(string[] args)
        {
            int index = Array.FindIndex(args, a => string.Equals(a, "--audio-probe", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int seconds))
                return Math.Clamp(seconds, 3, 120);
            return 15;
        }

        private sealed class ProbeVideoOptions
        {
            public int Width { get; init; } = 320;
            public int Height { get; init; } = 180;
            public int Fps { get; init; } = 10;
            public string Encoder { get; init; } = "libx264";
            public int Cqp { get; init; } = 35;
            public bool FromConfig { get; init; }
        }

        private static bool RunRepeated(int seconds, int repeat, string summaryLogPath, string[] args)
        {
            var config = LoadConfig();
            var video = ParseVideoOptions(args, config);
            var summary = new StringBuilder();
            summary.AppendLine($"Audio probe repeated run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            summary.AppendLine($"DurationSeconds={seconds}");
            summary.AppendLine($"Repeat={repeat}");
            summary.AppendLine($"Video={video.Width}x{video.Height}@{video.Fps}, encoder={video.Encoder}, cqp={video.Cqp}, fromConfig={video.FromConfig}");

            bool allOk = true;
            for (int i = 1; i <= repeat; i++)
            {
                string prefix = $"audio_probe_{i:00}";
                string runLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{prefix}.log");
                bool ok = Run(seconds, runLogPath, prefix, args);
                allOk &= ok;
                summary.AppendLine($"Run{i:00}={(ok ? "OK" : "FAILED")}; Log={runLogPath}");
            }

            summary.AppendLine($"Result={(allOk ? "OK" : "FAILED")}");
            File.WriteAllText(summaryLogPath, summary.ToString(), Encoding.UTF8);
            return allOk;
        }

        private static bool Run(int seconds, string logPath, string filePrefix, string[] args)
        {
            var config = LoadConfig();
            var video = ParseVideoOptions(args, config);
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            using var device = ResolveAudioEndpoint(config, devices);
            string wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{filePrefix}.wav");
            string mkvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{filePrefix}.mkv");
            string mp4Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{filePrefix}.mp4");
            string decodedWavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{filePrefix}_decoded.wav");
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            try { if (File.Exists(mkvPath)) File.Delete(mkvPath); } catch { }
            try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
            try { if (File.Exists(decodedWavPath)) File.Delete(decodedWavPath); } catch { }

            var log = new StringBuilder();
            log.AppendLine($"Audio probe started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"DurationSeconds={seconds}");
            log.AppendLine($"ConfiguredName={config.AudioDeviceName}");
            log.AppendLine($"ConfiguredEndpoint={config.AudioDeviceMoniker}");
            log.AppendLine($"SelectedDevice={device.FriendlyName}");
            log.AppendLine($"SelectedEndpoint={device.ID}");
            log.AppendLine($"VideoWidth={video.Width}");
            log.AppendLine($"VideoHeight={video.Height}");
            log.AppendLine($"VideoFps={video.Fps}");
            log.AppendLine($"VideoEncoder={video.Encoder}");
            log.AppendLine($"VideoCqp={video.Cqp}");
            log.AppendLine($"VideoFromConfig={video.FromConfig}");

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
                    : WriteSyntheticMkv(ffmpegPath, mkvPath, seconds, video);
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
            var mp4Info = ProbeMp4Mux(mkvPath, wavPath, mp4Path, decodedWavPath, seconds, config.AudioSyncOffsetMs);
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
                && mp4Info.Valid
                && mp4Info.DecodedTimeline.Valid
                && !LooksLikeShortPulseThenSilence(mp4Info.DecodedDurationSeconds, mp4Info.DecodedTimeline.ActiveWindowCount, mp4Info.DecodedTimeline.MaxConsecutiveActiveWindows, mp4Info.DecodedTimeline.LastActiveSecond);
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
            log.AppendLine($"Mp4DecodedWavPath={decodedWavPath}");
            log.AppendLine($"Mp4DecodedWavBytes={mp4Info.DecodedFileBytes}");
            log.AppendLine($"Mp4DecodedDurationSeconds={mp4Info.DecodedDurationSeconds:F2}");
            log.AppendLine($"Mp4DecodedFirstActiveSecond={mp4Info.DecodedTimeline.FirstActiveSecond:F1}");
            log.AppendLine($"Mp4DecodedLastActiveSecond={mp4Info.DecodedTimeline.LastActiveSecond:F1}");
            log.AppendLine($"Mp4DecodedActiveWindows={mp4Info.DecodedTimeline.ActiveWindowCount}");
            log.AppendLine($"Mp4DecodedMaxConsecutiveActiveWindows={mp4Info.DecodedTimeline.MaxConsecutiveActiveWindows}");
            log.AppendLine($"Mp4DecodedTimelineError={mp4Info.DecodedTimeline.Error ?? "(none)"}");
            log.AppendLine($"Mp4Error={mp4Info.Error ?? "(none)"}");
            log.AppendLine($"StoppedException={stoppedException?.Message ?? "(none)"}");
            log.AppendLine($"Result={(ok ? "OK" : "FAILED")}");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return ok;
        }

        private static (bool Valid, long FileBytes, bool AudioDecodeOk, long DecodedFileBytes, double DecodedDurationSeconds, (bool Valid, double FirstActiveSecond, double LastActiveSecond, int ActiveWindowCount, int MaxConsecutiveActiveWindows, string? Error) DecodedTimeline, string? Error) ProbeMp4Mux(string mkvPath, string wavPath, string mp4Path, string decodedWavPath, int seconds, int audioSyncOffsetMs)
        {
            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
                return (false, 0, false, 0, 0, (false, -1, -1, 0, 0, "ffmpeg.exe not found."), "ffmpeg.exe not found.");

            if (!File.Exists(mkvPath) || new FileInfo(mkvPath).Length <= 0)
                return (false, 0, false, 0, 0, (false, -1, -1, 0, 0, "Video MKV was not generated."), "Video MKV was not generated.");

            string muxArgs = MainViewModel.BuildMkvToMp4Args(mkvPath, wavPath, mp4Path, audioSyncOffsetMs);
            var mux = RunProcess(ffmpegPath, muxArgs, Math.Max(15000, seconds * 3000));
            if (!mux.Exited || mux.ExitCode != 0 || !File.Exists(mp4Path) || new FileInfo(mp4Path).Length <= 0)
                return (false, File.Exists(mp4Path) ? new FileInfo(mp4Path).Length : 0, false, 0, 0, (false, -1, -1, 0, 0, "Mux failed."), $"Mux failed: exited={mux.Exited}, exitCode={mux.ExitCode}, stderr={TrimForLog(mux.Stderr)}");

            string decodeArgs = $"-y -v error -i \"{mp4Path}\" -map 0:a:0 -ac 1 -ar 48000 -c:a pcm_s16le \"{decodedWavPath}\"";
            var decode = RunProcess(ffmpegPath, decodeArgs, Math.Max(15000, seconds * 3000));
            bool decodeOk = decode.Exited
                && decode.ExitCode == 0
                && File.Exists(decodedWavPath)
                && new FileInfo(decodedWavPath).Length > 0;
            var decodedInfo = ReadWavInfo(decodedWavPath);
            var decodedTimeline = ReadWavTimeline(decodedWavPath);
            string? error = decodeOk ? null : $"Audio decode failed: exited={decode.Exited}, exitCode={decode.ExitCode}, stderr={TrimForLog(decode.Stderr)}";
            return (decodeOk, new FileInfo(mp4Path).Length, decodeOk, decodedInfo.FileBytes, decodedInfo.DurationSeconds, decodedTimeline, error);
        }

        private static ProbeVideoOptions ParseVideoOptions(string[] args, AppConfig config)
        {
            bool fromConfig = args.Any(a => string.Equals(a, "--video-config", StringComparison.OrdinalIgnoreCase));
            int width = fromConfig ? Math.Clamp(config.FrameWidth, 16, 7680) : 320;
            int height = fromConfig ? Math.Clamp(config.FrameHeight, 16, 4320) : 180;
            int fps = fromConfig ? Math.Clamp(config.Fps, 1, 120) : 10;
            int cqp = fromConfig && config.VideoCqp > 0 ? config.VideoCqp : 35;
            string encoder = fromConfig ? ResolveProbeEncoder(config) : "libx264";

            string? size = GetArgValue(args, "--video-size");
            if (!string.IsNullOrWhiteSpace(size))
            {
                var parts = size.ToLowerInvariant().Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int parsedWidth) && int.TryParse(parts[1], out int parsedHeight))
                {
                    width = Math.Clamp(parsedWidth, 16, 7680);
                    height = Math.Clamp(parsedHeight, 16, 4320);
                }
            }

            string? fpsValue = GetArgValue(args, "--video-fps");
            if (int.TryParse(fpsValue, out int parsedFps))
                fps = Math.Clamp(parsedFps, 1, 120);

            string? encoderValue = GetArgValue(args, "--video-encoder");
            if (!string.IsNullOrWhiteSpace(encoderValue))
                encoder = encoderValue.Trim();

            string? cqpValue = GetArgValue(args, "--video-cqp");
            if (int.TryParse(cqpValue, out int parsedCqp))
                cqp = Math.Clamp(parsedCqp, 1, 63);

            return new ProbeVideoOptions
            {
                Width = width,
                Height = height,
                Fps = fps,
                Encoder = encoder,
                Cqp = cqp,
                FromConfig = fromConfig
            };
        }

        private static string? GetArgValue(string[] args, string name)
        {
            int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static string ResolveProbeEncoder(AppConfig config)
        {
            string codec = config.VideoCodec?.Trim().ToLowerInvariant() ?? "h264";
            if (codec != "h264" && codec != "h265" && codec != "av1") codec = "h264";
            string cpuEncoder = codec switch { "h265" => "libx265", "av1" => "libsvtav1", _ => "libx264" };
            var validated = new HashSet<string>(config.ValidatedEncodersCache ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            string gpu = EncodingHelper.NormalizeGpuSetting(config.GpuEncoder?.Trim().ToLowerInvariant() ?? "auto");

            if (gpu != "auto")
            {
                string requested = EncodingHelper.ResolveRequestedEncoder(gpu, codec);
                return requested == cpuEncoder || validated.Contains(requested) ? requested : cpuEncoder;
            }

            foreach (var candidateGpu in new[] { "nvidia", "amd", "intel" })
            {
                string candidate = EncodingHelper.ResolveRequestedEncoder(candidateGpu, codec);
                if (validated.Contains(candidate))
                    return candidate;
            }
            return cpuEncoder;
        }

        private static (bool Exited, int ExitCode, string Stderr) WriteSyntheticMkv(string ffmpegPath, string mkvPath, int seconds, ProbeVideoOptions video)
        {
            string args = MainViewModel.BuildFFmpegArgs(video.Width, video.Height, video.Fps, mkvPath, video.Encoder, false, video.Cqp);
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
            byte[] frame = new byte[video.Width * video.Height * 3];
            int totalFrames = seconds * video.Fps;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < totalFrames; i++)
                {
                    FillBgrFrame(frame, video.Width, video.Height, i);
                    process.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                    double targetMs = (i + 1) * 1000.0 / video.Fps;
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
            return !MainViewModel.IsAudioTimelineUsable(
                durationSeconds,
                -1,
                lastActiveSecond,
                activeWindowCount,
                maxConsecutiveActiveWindows,
                out _);
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
