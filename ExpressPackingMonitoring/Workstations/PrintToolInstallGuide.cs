using ExpressPackingMonitoring.Config;
using System.IO;
using System.Net;
using System.Text;

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
            File.WriteAllText(scriptPath, AddMonitorConnectPermission(script, monitorAddress), Encoding.UTF8);
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
        if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(monitorAddress)) return script;

        string value = monitorAddress.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host)) return script;

        string host = uri.Host;
        if (host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\')) return script;
        string directive = $"// @connect      {host}";
        if (script.Contains(directive, StringComparison.Ordinal)) return script;

        const string marker = "// @connect      localhost";
        int markerIndex = script.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return script;
        int lineEnd = script.IndexOf('\n', markerIndex);
        if (lineEnd < 0) return script + Environment.NewLine + directive;
        return script.Insert(lineEnd + 1, directive + "\n");
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
