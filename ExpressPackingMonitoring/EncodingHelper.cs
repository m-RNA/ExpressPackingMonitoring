#nullable disable
using System;
using System.Collections.Generic;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring
{
    internal static class EncodingHelper
    {
        public static string NormalizeGpuSetting(string setting)
        {
            return (setting?.Trim().ToLowerInvariant()) switch
            {
                "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" or "nvidia" => "nvidia",
                "h264_amf" or "hevc_amf" or "av1_amf" or "amd" => "amd",
                "h264_qsv" or "hevc_qsv" or "av1_qsv" or "intel" => "intel",
                "libx264" or "libx265" or "libsvtav1" or "cpu" => "cpu",
                _ => setting ?? "auto"
            };
        }

        public static string GetCodecFromEncoder(string encoder)
        {
            return encoder switch
            {
                "libx264" or "h264_nvenc" or "h264_amf" or "h264_qsv" => "h264",
                "libx265" or "hevc_nvenc" or "hevc_amf" or "hevc_qsv" => "h265",
                "libsvtav1" or "av1_nvenc" or "av1_amf" or "av1_qsv" => "av1",
                _ => "h264"
            };
        }

        public static string GetCodecLabel(string codec)
        {
            return codec switch
            {
                "h265" => "H.265 / HEVC",
                "av1" => "AV1",
                _ => "H.264"
            };
        }

        public static string GetEncoderLabel(string encoder)
        {
            return encoder switch
            {
                "h264_nvenc" => "N 264",
                "h264_amf" => "A 264",
                "h264_qsv" => "I 264",
                "libx264" => "C 264",
                "hevc_nvenc" => "N 265",
                "hevc_amf" => "A 265",
                "hevc_qsv" => "I 265)",
                "libx265" => "C 265)",
                "av1_nvenc" => "N AV1",
                "av1_amf" => "A AV1",
                "av1_qsv" => "I AV1",
                "libsvtav1" => "C AV1",
                _ => encoder
            };
        }

        public static string ResolveRequestedEncoder(string gpu, string codec)
        {
            gpu = NormalizeGpuSetting(gpu ?? "auto");
            return (gpu, codec) switch
            {
                ("nvidia", "h264") => "h264_nvenc",
                ("nvidia", "h265") => "hevc_nvenc",
                ("nvidia", "av1") => "av1_nvenc",
                ("amd", "h264") => "h264_amf",
                ("amd", "h265") => "hevc_amf",
                ("amd", "av1") => "av1_amf",
                ("intel", "h264") => "h264_qsv",
                ("intel", "h265") => "hevc_qsv",
                ("intel", "av1") => "av1_qsv",
                ("cpu", "h264") => "libx264",
                ("cpu", "h265") => "libx265",
                ("cpu", "av1") => "libsvtav1",
                _ => codec switch
                {
                    "h265" => "hevc_nvenc",
                    "av1" => "av1_nvenc",
                    _ => "h264_nvenc"
                }
            };
        }

        public static string ResolveFallbackEncoder(string gpu, string codec, HashSet<string> validated)
        {
            validated ??= new HashSet<string>();
            string cpuEncoder = codec switch
            {
                "h265" => "libx265",
                "av1" => "libsvtav1",
                _ => "libx264"
            };

            gpu = NormalizeGpuSetting(gpu ?? "auto");
            if (gpu != "auto")
            {
                string requested = ResolveRequestedEncoder(gpu, codec);
                if (requested == cpuEncoder || validated.Contains(requested))
                    return requested;
                if (validated.Contains(cpuEncoder))
                    return cpuEncoder;
                return "libx264";
            }

            foreach (var candidateGpu in new[] { "nvidia", "amd", "intel" })
            {
                string candidate = ResolveRequestedEncoder(candidateGpu, codec);
                if (validated.Contains(candidate))
                    return candidate;
            }

            if (validated.Contains(cpuEncoder))
                return cpuEncoder;

            return "libx264";
        }

        public static void ApplyEncoderSelectionToConfig(AppConfig config, string encoder)
        {
            config.VideoCodec = GetCodecFromEncoder(encoder);
            config.GpuEncoder = NormalizeGpuSetting(encoder);
        }
    }
}