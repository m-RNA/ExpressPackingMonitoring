using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
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
    }
}
