using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring;

internal static class PrintToolInstallGuide
{
    private const string TemplateFileName = "kuaidizs-install-guide.html";
    private const string ScriptFileName = "快递助手订单推送.user.js";

    public static string CreateLocalGuide(string monitorAddress)
    {
        string guideDir = AppPaths.GuideCacheDir;
        Directory.CreateDirectory(guideDir);
        string guidePath = Path.Combine(guideDir, TemplateFileName);
        string sourceScriptPath = ResolveUserscriptPath();
        string scriptPath = Path.Combine(guideDir, ScriptFileName);
        if (File.Exists(sourceScriptPath))
        {
            string script = File.ReadAllText(sourceScriptPath, Encoding.UTF8);
            IEnumerable<string> addresses = new[] { monitorAddress }
                .Concat(MobileOrderReceiverRegistry.GetDefaultAuthorities());
            File.WriteAllText(scriptPath, AddMonitorConnectPermissions(script, addresses), Encoding.UTF8);
        }
        string scriptUrl = File.Exists(scriptPath) ? new Uri(scriptPath).AbsoluteUri : "";
        string html = Render(monitorAddress, BuildScriptLink(scriptUrl));
        File.WriteAllText(guidePath, html, Encoding.UTF8);
        return guidePath;
    }

    public static string RenderForWeb(string monitorAddress, string scriptUrl)
    {
        return Render(monitorAddress, BuildScriptLink(scriptUrl));
    }

    public static string ResolveUserscriptPath()
    {
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Scripts", ScriptFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Scripts", ScriptFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Scripts", ScriptFileName))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    internal static string AddMonitorConnectPermission(string script, string monitorAddress)
    {
        return AddMonitorConnectPermissions(script, new[] { monitorAddress });
    }

    internal static string AddMonitorConnectPermissions(string script, IEnumerable<string> monitorAddresses)
    {
        if (string.IsNullOrWhiteSpace(script)) return script;

        List<Uri> addresses = NormalizeMonitorAddresses(monitorAddresses);
        if (addresses.Count == 0) return script;

        string customized = script.Replace(
            "const INSTALL_MONITOR_ADDRESSES = [];",
            $"const INSTALL_MONITOR_ADDRESSES = {JsonSerializer.Serialize(addresses.Select(uri => uri.Authority))};",
            StringComparison.Ordinal);
        customized = customized.Replace(
            "const INSTALL_PRIMARY_MONITOR_ADDRESS = '';",
            $"const INSTALL_PRIMARY_MONITOR_ADDRESS = {JsonSerializer.Serialize(addresses[0].Authority)};",
            StringComparison.Ordinal);
        string newline = customized.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        const string marker = "// @connect      localhost";
        int markerIndex = customized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return customized;
        int lineEnd = customized.IndexOf('\n', markerIndex);
        int insertIndex = lineEnd < 0 ? customized.Length : lineEnd + 1;
        string prefix = lineEnd < 0 ? newline : "";

        foreach (string host in addresses.Select(uri => uri.Host).Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            string directive = $"// @connect      {host}";
            if (!customized.Contains(directive, StringComparison.Ordinal))
                customized = customized.Insert(insertIndex, prefix + directive + newline);
        }

        return customized;
    }

    internal static List<Uri> NormalizeMonitorAddresses(IEnumerable<string>? monitorAddresses)
    {
        var result = new List<Uri>();
        foreach (string rawAddress in monitorAddresses ?? Array.Empty<string>())
        {
            foreach (string part in (rawAddress ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string value = part.Contains("://", StringComparison.Ordinal) ? part : "http://" + part;
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                    || !IsAllowedMonitorHost(uri.Host)
                    || uri.Port is <= 0 or > 65535)
                    continue;

                int port = uri.IsDefaultPort ? 5280 : uri.Port;
                var normalized = new UriBuilder(Uri.UriSchemeHttp, uri.Host, port).Uri;
                if (result.Any(item => string.Equals(item.Authority, normalized.Authority, StringComparison.OrdinalIgnoreCase)))
                    continue;

                result.Add(normalized);
                if (result.Count >= 8) break;
            }

            if (result.Count >= 8) break;
        }

        return result;
    }

    private static bool IsAllowedMonitorHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 127
            || bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static string Render(string monitorAddress, string scriptLink)
    {
        string template = LoadTemplate();
        return template
            .Replace("{{scriptLink}}", scriptLink, StringComparison.Ordinal)
            .Replace("{{address}}", WebUtility.HtmlEncode(monitorAddress), StringComparison.Ordinal);
    }

    private static string LoadTemplate()
    {
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Web", TemplateFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Web", TemplateFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Web", TemplateFileName))
        };

        string? path = candidates.FirstOrDefault(File.Exists);
        return path == null ? MissingTemplateHtml : File.ReadAllText(path, Encoding.UTF8);
    }

    private static string BuildScriptLink(string scriptUrl)
    {
        return string.IsNullOrWhiteSpace(scriptUrl)
            ? "<div class=\"warn\">未找到订单备注插件脚本文件，请确认发布包内包含 Scripts 文件夹。</div>"
            : $"<a class=\"primary\" href=\"{WebUtility.HtmlEncode(scriptUrl)}\" target=\"_blank\" rel=\"noopener\">打开订单备注插件安装页</a>";
    }

    private const string MissingTemplateHtml = """
<!doctype html>
<html lang="zh-CN">
<head><meta charset="utf-8"><title>安装向导缺失</title></head>
<body style="font-family: Microsoft YaHei UI, sans-serif; padding: 32px;">
  <h1>安装向导文件缺失</h1>
  <p>未找到 Web/kuaidizs-install-guide.html，请检查程序发布目录是否完整。</p>
</body>
</html>
""";
}
