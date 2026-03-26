using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private static string QueryFFmpegEncoders(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output ?? "";
            }
            catch { return ""; }
        }

        public void ResetEncoderDetect()
        {
            if (_isEncoderDetectRunning)
            {
                ShowToast("⏳ 检测已在运行中...");
                return;
            }

            Task.Run(() =>
            {
                _isEncoderDetectRunning = true;
                ShowToast("🔄 正在重新检测 GPU 编码器，请稍候...");

                var (options, validated) = DetectAvailableEncodersSync();
                CachedEncoderOptions = options;
                ValidatedEncoders = validated;

                Config.EncoderOptionsCache = options;
                Config.ValidatedEncodersCache = validated.ToList();
                Config.IsEncoderDetected = true;
                SaveConfig();

                _isEncoderDetectRunning = false;
                ShowToast("✅ 编码器重新检测完成");
            });
        }

        private static (bool ok, string stderr) TestEncoder(string ffmpegPath, string encoder)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f lavfi -i color=black:s=256x256:d=0.1 -frames:v 2 -an -pix_fmt yuv420p -c:v {encoder} -f null -",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Process.Start returned null");
                string stderr = proc.StandardError.ReadToEnd();
                bool exited = proc.WaitForExit(15000);
                int exitCode = exited ? proc.ExitCode : -999;
                return (exited && exitCode == 0, $"exit={exitCode} stderr={stderr}");
            }
            catch (Exception ex) { return (false, $"exception: {ex.Message}"); }
        }

        public static (List<GpuEncoderOption> options, HashSet<string> validated) DetectAvailableEncodersSync()
        {
            var log = new StringBuilder();
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === GPU 编码器检测开始 ===");

            var list = new List<GpuEncoderOption>
            {
                new GpuEncoderOption { Value = "auto", DisplayName = "自动检测（优先独显）" },
                new GpuEncoderOption { Value = "cpu", DisplayName = "CPU 软编码" }
            };
            var validated = new HashSet<string> { "libx264", "libx265" };

            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            log.AppendLine($"FFmpeg 路径: {ffmpegPath}");
            log.AppendLine($"FFmpeg 存在: {File.Exists(ffmpegPath)}");

            if (!File.Exists(ffmpegPath))
            {
                log.AppendLine("⚠ FFmpeg 不存在，跳过检测");
                WriteEncoderLog(log);
                return (list, validated);
            }

            string output = QueryFFmpegEncoders(ffmpegPath);
            log.AppendLine($"ffmpeg -encoders 输出长度: {output.Length}");

            var gpuGroups = new[]
            {
                (gpu: "nvidia", label: "NVIDIA GPU (NVENC)",  encs: new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" }),
                (gpu: "amd",    label: "AMD GPU (AMF)",       encs: new[] { "h264_amf",   "hevc_amf",   "av1_amf" }),
                (gpu: "intel",  label: "Intel GPU (QSV)",     encs: new[] { "h264_qsv",   "hevc_qsv",   "av1_qsv" })
            };

            foreach (var (gpu, label, encs) in gpuGroups)
            {
                log.AppendLine($"\n=== {label} ===");
                bool anyPassed = false;

                foreach (var enc in encs)
                {
                    bool inList = output.Contains(enc);
                    log.AppendLine($"  --- {enc} ---");
                    log.AppendLine($"    ffmpeg -encoders 包含: {inList}");

                    if (!inList)
                    {
                        log.AppendLine($"    跳过试编码（不在编码器列表中）");
                        continue;
                    }

                    var (testOk, testDetail) = TestEncoder(ffmpegPath, enc);
                    log.AppendLine($"    试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                    log.AppendLine($"    详情: {testDetail}");

                    if (testOk)
                    {
                        validated.Add(enc);
                        anyPassed = true;
                    }
                }

                if (anyPassed)
                    list.Insert(list.Count - 1, new GpuEncoderOption { Value = gpu, DisplayName = label });
            }

            {
                log.AppendLine($"\n=== CPU AV1 (libsvtav1) ===");
                bool svtInList = output.Contains("libsvtav1");
                log.AppendLine($"  ffmpeg -encoders 包含: {svtInList}");
                if (svtInList)
                {
                    var (testOk, testDetail) = TestEncoder(ffmpegPath, "libsvtav1");
                    log.AppendLine($"  试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                    log.AppendLine($"  详情: {testDetail}");
                    if (testOk) validated.Add("libsvtav1");
                }
                else
                {
                    log.AppendLine($"  跳过试编码（不在编码器列表中）");
                }
            }

            log.AppendLine($"\nGPU 选项: {string.Join(", ", list.Select(e => e.Value))}");
            log.AppendLine($"已验证编码器: {string.Join(", ", validated)}");
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 检测结束 ===");
            WriteEncoderLog(log);
            return (list, validated);
        }

        private static void WriteEncoderLog(StringBuilder log)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "encoder_detect.log");
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
