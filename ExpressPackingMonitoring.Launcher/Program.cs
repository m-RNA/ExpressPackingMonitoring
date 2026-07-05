using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string AppRelativePath = "app\\ExpressPackingMonitoring.exe";
    private const string UpdatesDirName = "updates";
    private const string StagingDirName = "staging";
    private const string BackupDirName = "backup";
    private const string StateFileName = "update_state.json";
    private const string LogFileName = "update.log";
    private const string UpdateUrlKey = "UPDATE_CHECK_URL";
    private const string DefaultCheckUrl = "https://api.github.com/repos/m-RNA/ExpressPackingMonitoring/releases/latest";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    [STAThread]
    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        CheckAndInstallPatchUpdateAsync(baseDir).GetAwaiter().GetResult();

        string appPath = Path.Combine(baseDir, AppRelativePath);
        return StartApp(baseDir, appPath, args);
    }

    private static async Task CheckAndInstallPatchUpdateAsync(string baseDir)
    {
        try
        {
            UpdateState state = LoadState(baseDir);
            if (!state.AutoCheckUpdate)
                return;

            string checkUrl = GetUpdateCheckUrl(baseDir);
            if (string.IsNullOrWhiteSpace(checkUrl))
                return;

            using JsonDocument release = await GetJsonAsync(checkUrl);
            JsonElement releaseRoot = release.RootElement;
            string tagName = ReadString(releaseRoot, "tag_name");
            if (string.IsNullOrWhiteSpace(tagName))
                return;

            string latestVersion = NormalizeVersion(tagName);
            if (CompareVersions(latestVersion, state.CurrentVersion) <= 0)
                return;

            AssetInfo? manifestAsset = FindUpdateManifestAsset(releaseRoot, latestVersion);
            if (manifestAsset == null)
                return;

            using JsonDocument updateManifest = await GetJsonAsync(manifestAsset.Url);
            JsonElement updateRoot = updateManifest.RootElement;
            string fullDownloadPage = ReadString(updateRoot, "full_download_page");
            if (string.IsNullOrWhiteSpace(fullDownloadPage))
                fullDownloadPage = ReadString(updateRoot, "release_page");

            string baselineVersion = NormalizeVersion(ReadString(updateRoot, "patch_baseline_version"));
            if (!ReadBoolean(updateRoot, "patch_supported"))
            {
                PromptFullUpdate("本版本需要完整更新，请手动下载完整包。", fullDownloadPage);
                return;
            }

            if (!string.IsNullOrWhiteSpace(baselineVersion) &&
                CompareVersions(state.CurrentVersion, baselineVersion) < 0)
            {
                PromptFullUpdate("当前版本过旧，需要手动下载完整包。", fullDownloadPage);
                return;
            }

            PatchPackageInfo package = ReadPatchPackageInfo(updateRoot);
            if (string.IsNullOrWhiteSpace(package.Url) || string.IsNullOrWhiteSpace(package.Sha256))
            {
                PromptFullUpdate("本版本需要完整更新，请手动下载完整包。", fullDownloadPage);
                return;
            }

            await DownloadAndInstallPatchAsync(baseDir, latestVersion, package, fullDownloadPage);
        }
        catch (Exception ex)
        {
            WriteLog(baseDir, "自动检查更新失败：" + ex);
        }
    }

    private static async Task DownloadAndInstallPatchAsync(
        string baseDir,
        string latestVersion,
        PatchPackageInfo package,
        string fullDownloadPage)
    {
        string updatesDir = Path.Combine(baseDir, UpdatesDirName);
        string stagingDir = Path.Combine(updatesDir, StagingDirName);
        string patchZipPath = Path.Combine(updatesDir, "patch.zip");
        string tmpPath = patchZipPath + ".tmp";

        try
        {
            Directory.CreateDirectory(updatesDir);
            SafeDeleteDirectory(stagingDir);
            DeleteFileIfExists(patchZipPath);
            DeleteFileIfExists(tmpPath);

            await DownloadFileAsync(package.Url, tmpPath);
            string actualHash = ComputeSha256(tmpPath);
            if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Patch 包 SHA256 校验失败");

            File.Move(tmpPath, patchZipPath);
            ZipFile.ExtractToDirectory(patchZipPath, stagingDir, overwriteFiles: true);
            InstallPatchFromStaging(baseDir, stagingDir, latestVersion);
            SafeDeleteDirectory(stagingDir);
            DeleteFileIfExists(patchZipPath);
            WriteLog(baseDir, $"Patch 更新完成：{latestVersion}");
        }
        catch (Exception ex)
        {
            WriteLog(baseDir, "Patch 更新失败：" + ex);
            SafeDeleteDirectory(stagingDir);
            DeleteFileIfExists(tmpPath);
            DeleteFileIfExists(patchZipPath);
            PromptFullUpdate("增量更新失败，请手动下载完整包。", fullDownloadPage);
        }
    }

    private static void InstallPatchFromStaging(string baseDir, string stagingDir, string latestVersion)
    {
        string manifestPath = Path.Combine(stagingDir, "patch_manifest.json");
        string patchFilesDir = Path.Combine(stagingDir, "files");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Patch 包缺少 patch_manifest.json");
        if (!Directory.Exists(patchFilesDir))
            throw new InvalidOperationException("Patch 包缺少 files 目录");

        List<PatchFile> files = ReadPatchFiles(manifestPath);
        ValidatePatchFiles(patchFilesDir, files);

        string appDir = Path.Combine(baseDir, "app");
        string backupRoot = Path.Combine(baseDir, UpdatesDirName, BackupDirName);
        SafeDeleteDirectory(backupRoot);
        Directory.CreateDirectory(backupRoot);

        var backups = new List<FileBackup>();
        try
        {
            foreach (PatchFile file in files)
            {
                string targetPath = GetSafeChildPath(appDir, file.RelativePath);
                string backupPath = GetSafeChildPath(backupRoot, file.RelativePath);
                bool existed = File.Exists(targetPath);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(targetPath, backupPath, overwrite: true);
                }

                backups.Add(new FileBackup(targetPath, backupPath, existed));
            }

            foreach (PatchFile file in files)
            {
                string sourcePath = GetSafeChildPath(patchFilesDir, file.RelativePath);
                string targetPath = GetSafeChildPath(appDir, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            UpdateCurrentVersion(baseDir, latestVersion);
            SafeDeleteDirectory(backupRoot);
        }
        catch
        {
            RollbackFiles(backups);
            throw;
        }
    }

    private static List<PatchFile> ReadPatchFiles(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Patch manifest 缺少 files 列表");

        var files = new List<PatchFile>();
        foreach (JsonElement fileElement in filesElement.EnumerateArray())
        {
            string relativePath = NormalizeRelativePath(ReadString(fileElement, "path"));
            string sha256 = ReadString(fileElement, "sha256");
            long size = ReadInt64(fileElement, "size");
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(sha256))
                throw new InvalidOperationException("Patch manifest 文件条目不完整");

            files.Add(new PatchFile(relativePath, sha256, size));
        }

        return files;
    }

    private static void ValidatePatchFiles(string patchFilesDir, List<PatchFile> files)
    {
        foreach (PatchFile file in files)
        {
            string sourcePath = GetSafeChildPath(patchFilesDir, file.RelativePath);
            if (!File.Exists(sourcePath))
                throw new InvalidOperationException($"Patch 文件不存在：{file.RelativePath}");

            FileInfo info = new(sourcePath);
            if (file.Size >= 0 && info.Length != file.Size)
                throw new InvalidOperationException($"Patch 文件大小校验失败：{file.RelativePath}");

            string hash = ComputeSha256(sourcePath);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Patch 文件 SHA256 校验失败：{file.RelativePath}");
        }
    }

    private static void RollbackFiles(List<FileBackup> backups)
    {
        foreach (FileBackup backup in backups.AsEnumerable().Reverse())
        {
            try
            {
                if (backup.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup.TargetPath)!);
                    File.Copy(backup.BackupPath, backup.TargetPath, overwrite: true);
                }
                else
                {
                    DeleteFileIfExists(backup.TargetPath);
                }
            }
            catch
            {
            }
        }
    }

    private static int StartApp(string baseDir, string appPath, string[] args)
    {
        if (!File.Exists(appPath))
        {
            ShowError($"未找到主程序：{AppRelativePath}\n\n请确认 app 文件夹与本启动程序放在同一目录。");
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? baseDir,
                UseShellExecute = false
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowError($"启动主程序失败：\n{ex.Message}");
            return 1;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
        using HttpResponseMessage response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync();
        await using Stream target = File.Create(path);
        await source.CopyToAsync(target);
    }

    private static AssetInfo? FindUpdateManifestAsset(JsonElement releaseRoot, string latestVersion)
    {
        if (!releaseRoot.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string preferred = $"update_v{latestVersion}.json";
        AssetInfo? fallback = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = ReadString(asset, "name");
            string url = ReadString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url))
                url = ReadString(asset, "url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;

            if (string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                return new AssetInfo(name, url);
            if (fallback == null && string.Equals(name, "update.json", StringComparison.OrdinalIgnoreCase))
                fallback = new AssetInfo(name, url);
        }

        return fallback;
    }

    private static PatchPackageInfo ReadPatchPackageInfo(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("patch_package", out JsonElement package) || package.ValueKind != JsonValueKind.Object)
            return new PatchPackageInfo("", "");

        return new PatchPackageInfo(
            ReadString(package, "url"),
            ReadString(package, "sha256"));
    }

    private static UpdateState LoadState(string baseDir)
    {
        string path = Path.Combine(baseDir, StateFileName);
        if (!File.Exists(path))
            return new UpdateState("0.0.0", true);

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonElement root = document.RootElement;
            string version = ReadString(root, "current_version");
            bool autoCheck = true;
            if (root.TryGetProperty("auto_check_update", out JsonElement autoValue) &&
                (autoValue.ValueKind == JsonValueKind.True || autoValue.ValueKind == JsonValueKind.False))
            {
                autoCheck = autoValue.GetBoolean();
            }

            return new UpdateState(string.IsNullOrWhiteSpace(version) ? "0.0.0" : NormalizeVersion(version), autoCheck);
        }
        catch
        {
            return new UpdateState("0.0.0", true);
        }
    }

    private static void UpdateCurrentVersion(string baseDir, string version)
    {
        UpdateState state = LoadState(baseDir);
        state.CurrentVersion = NormalizeVersion(version);
        SaveState(baseDir, state);
    }

    private static void SaveState(string baseDir, UpdateState state)
    {
        string path = Path.Combine(baseDir, StateFileName);
        string version = state.CurrentVersion.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string json = "{\n" +
            $"  \"current_version\": \"{version}\",\n" +
            $"  \"auto_check_update\": {state.AutoCheckUpdate.ToString().ToLowerInvariant()}\n" +
            "}\n";

        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string GetUpdateCheckUrl(string baseDir)
    {
        string? value = Environment.GetEnvironmentVariable(UpdateUrlKey);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        foreach (string path in new[] { Path.Combine(baseDir, ".env"), Path.Combine(Environment.CurrentDirectory, ".env") })
        {
            if (!File.Exists(path))
                continue;

            foreach (string rawLine in File.ReadLines(path, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                if (string.Equals(key, UpdateUrlKey, StringComparison.OrdinalIgnoreCase))
                    return line[(separator + 1)..].Trim().Trim('"', '\'');
            }
        }

        return DefaultCheckUrl;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return "";

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return false;

        return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.False ? false : false);
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return -1;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) ? result : -1;
    }

    private static int CompareVersions(string latest, string current)
    {
        Version latestVersion = ParseVersion(latest);
        Version currentVersion = ParseVersion(current);
        return latestVersion.CompareTo(currentVersion);
    }

    private static Version ParseVersion(string value)
    {
        string normalized = NormalizeVersion(value);
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
            _ => new Version(0, 0, 0)
        };
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = value.Trim();
        return normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? normalized[1..] : normalized;
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('/', '\\').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
            throw new InvalidOperationException("Patch 文件路径非法");

        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == "." || part == ".."))
            throw new InvalidOperationException("Patch 文件路径非法");

        return string.Join('\\', parts);
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(rootFullPath, normalized));
        if (!fullPath.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch 文件路径越界");

        return fullPath;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void PromptFullUpdate(string message, string fullDownloadPage)
    {
        ShowError(message);
        if (string.IsNullOrWhiteSpace(fullDownloadPage))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(fullDownloadPage) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void WriteLog(string baseDir, string message)
    {
        try
        {
            string updatesDir = Path.Combine(baseDir, UpdatesDirName);
            Directory.CreateDirectory(updatesDir);
            File.AppendAllText(
                Path.Combine(updatesDir, LogFileName),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void ShowError(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "打包监控", 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private sealed record UpdateState(string CurrentVersion, bool AutoCheckUpdate)
    {
        public string CurrentVersion { get; set; } = CurrentVersion;
        public bool AutoCheckUpdate { get; set; } = AutoCheckUpdate;
    }

    private sealed record AssetInfo(string Name, string Url);

    private sealed record PatchPackageInfo(string Url, string Sha256);

    private sealed record PatchFile(string RelativePath, string Sha256, long Size);

    private sealed record FileBackup(string TargetPath, string BackupPath, bool Existed);
}
