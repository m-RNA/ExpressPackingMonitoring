using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Config;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.Services
{
    public sealed class UpdateCheckResult
    {
        public bool HasUpdate { get; init; }
        public string LatestVersion { get; init; } = "";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
    }

    public sealed class UpdateCheckService
    {
        private static readonly TimeSpan ManualDebounce = TimeSpan.FromSeconds(300);
        private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromDays(1);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        public async Task<UpdateCheckResult> CheckManualAsync(CancellationToken cancellationToken = default)
        {
            UpdateCheckCache? cache = LoadCache();
            if (TryGetCachedResult(cache, ManualDebounce, out UpdateCheckResult cached))
            {
                RuntimeLog.Info("Update", "Manual update check debounced, using cached success result");
                return cached;
            }

            return await CheckAndCacheAsync(cache, cancellationToken);
        }

        public async Task<UpdateCheckResult> CheckAutomaticAsync(CancellationToken cancellationToken = default)
        {
            UpdateCheckCache? cache = LoadCache();
            if (TryGetCachedResult(cache, AutoCheckInterval, out UpdateCheckResult cached))
            {
                RuntimeLog.Info("Update", "Automatic update check skipped, using cached success result");
                return cached;
            }

            return await CheckAndCacheAsync(cache, cancellationToken);
        }

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            return await CheckAndCacheAsync(LoadCache(), cancellationToken);
        }

        private async Task<UpdateCheckResult> CheckAndCacheAsync(UpdateCheckCache? cache, CancellationToken cancellationToken)
        {
            try
            {
                UpdateCheckResult result = await FetchLatestReleaseAsync(cancellationToken);
                SaveCache(result);
                return result;
            }
            catch (Exception ex) when (IsRateLimitException(ex) && TryGetCachedResult(cache, out UpdateCheckResult cached))
            {
                RuntimeLog.Warn("Update", $"Update check rate limited, using cached success result: {ex.Message}");
                return cached;
            }
        }

        private async Task<UpdateCheckResult> FetchLatestReleaseAsync(CancellationToken cancellationToken)
        {
            string url = UpdateCheckOptions.GetUpdateCheckUrl();
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("更新检查地址未配置");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tagName = ReadString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tagName))
                throw new InvalidOperationException("最新版本号为空");

            int compare = CompareVersions(tagName, AppVersion.Current);
            if (compare <= 0)
            {
                return new UpdateCheckResult
                {
                    HasUpdate = false,
                    LatestVersion = tagName,
                    Title = ReadString(root, "name"),
                    Body = ReadString(root, "body")
                };
            }

            string body = ReadString(root, "body");
            string releaseUrl = ReadString(root, "html_url");
            string assetUrl = ReadFirstAssetUrl(root);

            return new UpdateCheckResult
            {
                HasUpdate = true,
                LatestVersion = tagName,
                Title = ReadString(root, "name"),
                Body = body,
                DownloadUrl = ChooseDownloadUrl(body, releaseUrl, assetUrl)
            };
        }

        private static bool TryGetCachedResult(UpdateCheckCache? cache, TimeSpan maxAge, out UpdateCheckResult result)
        {
            result = default!;
            if (!TryGetCachedResult(cache, out UpdateCheckResult cached)) return false;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - cache!.LastSuccessUtc > maxAge) return false;

            result = cached;
            return true;
        }

        private static bool TryGetCachedResult(UpdateCheckCache? cache, out UpdateCheckResult result)
        {
            result = default!;
            if (cache?.Result == null) return false;
            if (!string.Equals(cache.CurrentVersion, AppVersion.Current, StringComparison.OrdinalIgnoreCase)) return false;

            result = cache.Result;
            return true;
        }

        private static bool IsRateLimitException(Exception ex)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
                return true;

            string message = ex.Message ?? "";
            return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase);
        }

        private static UpdateCheckCache? LoadCache()
        {
            try
            {
                string path = AppPaths.UpdateCheckCachePath;
                if (!File.Exists(path)) return null;

                return JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Update", $"Read update check cache failed: {ex.Message}");
                return null;
            }
        }

        private static void SaveCache(UpdateCheckResult result)
        {
            try
            {
                var cache = new UpdateCheckCache
                {
                    CurrentVersion = AppVersion.Current,
                    LastSuccessUtc = DateTimeOffset.UtcNow,
                    Result = result
                };

                string path = AppPaths.UpdateCheckCachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOptions));
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Update", $"Write update check cache failed: {ex.Message}");
            }
        }

        public static void OpenDownloadPage(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("下载地址为空");

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static string ChooseDownloadUrl(string body, string releaseUrl, string assetUrl)
        {
            string baiduUrl = ExtractBaiduPanUrl(body);
            if (!string.IsNullOrWhiteSpace(baiduUrl)) return baiduUrl;
            if (!string.IsNullOrWhiteSpace(releaseUrl)) return releaseUrl;
            return assetUrl;
        }

        private static string ExtractBaiduPanUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            Match match = Regex.Match(
                text,
                @"https?://(?:pan|yun)\.baidu\.com/[^\s\)）\]】>""']+",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Value.TrimEnd('.', '。', ',', '，') : "";
        }

        private static string ReadFirstAssetUrl(JsonElement root)
        {
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return "";

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string url = ReadString(asset, "browser_download_url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }

            return "";
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return "";

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        }

        private static int CompareVersions(string latest, string current)
        {
            Version latestVersion = ParseVersion(latest);
            Version currentVersion = ParseVersion(current);
            return latestVersion.CompareTo(currentVersion);
        }

        private static Version ParseVersion(string value)
        {
            string normalized = value.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[1..];

            int suffixIndex = normalized.IndexOfAny(new[] { '+', '-' });
            if (suffixIndex >= 0)
                normalized = normalized[..suffixIndex];

            int[] parts = normalized
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out int n) ? n : throw new FormatException($"版本号格式异常: {value}"))
                .ToArray();

            return parts.Length switch
            {
                1 => new Version(parts[0], 0, 0),
                2 => new Version(parts[0], parts[1], 0),
                >= 3 => new Version(parts[0], parts[1], parts[2]),
                _ => throw new FormatException($"版本号格式异常: {value}")
            };
        }

        private sealed class UpdateCheckCache
        {
            public string CurrentVersion { get; set; } = "";
            public DateTimeOffset LastSuccessUtc { get; set; }
            public UpdateCheckResult? Result { get; set; }
        }
    }
}
