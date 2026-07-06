using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string AppRelativePath = "app\\ExpressPackingMonitoring.exe";
    private const string AppDllRelativePath = "app\\ExpressPackingMonitoring.dll";
    private const string UpdateUrlKey = "UPDATE_CHECK_URL";
    private const string DefaultCheckUrlMetadataKey = "LauncherDefaultUpdateCheckUrl";
    private const string FallbackCheckUrl = "https://api.github.com/repos/m-RNA/ExpressPackingMonitoring/releases/latest";
    private const string DefaultPatchDownloadBaseUrl = "https://github.com/m-RNA/ExpressPackingMonitoring/releases/download";
    private const string PatchPackageType = "baseline_patch";
    private const string UpdateMutexName = @"Local\ExpressPackingMonitoring.Launcher.Update";
    private const int GithubDownloadFailureFallbackThreshold = 3;
    private const string InstanceNamePrefix = "ExpressPackingMonitoring";
    private const string CameraMonitorRole = "CameraMonitor";
    private const string PrintStationRole = "PrintStation";
    private static readonly TimeSpan NetworkUpdateTimeout = TimeSpan.FromSeconds(75);
    private const uint InfoIcon = 0x00000040;
    private const uint ErrorIcon = 0x00000010;
    private const uint DialogTimeoutMs = 60000;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    [STAThread]
    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        string appPath = Path.Combine(baseDir, AppRelativePath);
        bool appAlreadyRunning = IsAppRunning(appPath);
        bool hasPendingUpdate = HasPendingUpdate();
        UpdateNotification? notification = appAlreadyRunning ? null : RunExclusivePendingInstall(baseDir);

        if (appAlreadyRunning)
        {
            if (!TryActivateExistingApp(args))
            {
                int startResult = StartApp(baseDir, appPath, args);
                if (startResult != 0)
                    return startResult;
            }
        }
        else
        {
            int startResult = StartApp(baseDir, appPath, args);
            if (startResult != 0)
                return startResult;
        }

        Thread? backgroundUpdateThread = null;
        if (notification == null && !hasPendingUpdate)
            backgroundUpdateThread = StartBackgroundUpdateDownload(baseDir);

        Thread? notificationThread = null;
        if (notification != null)
        {
            notificationThread = ShowTimedMessageAsync(
                notification.Message,
                notification.IsError ? ErrorIcon : InfoIcon);
        }

        backgroundUpdateThread?.Join();
        notificationThread?.Join();
        return 0;
    }

    private static UpdateNotification? RunExclusivePendingInstall(string baseDir)
    {
        return RunExclusiveUpdateWork("pending 安装", () =>
        {
            try
            {
                return InstallPendingUpdate(baseDir);
            }
            catch (Exception ex)
            {
                WriteLog("安装 pending 更新失败：" + ex);
                return null;
            }
        });
    }

    private static Thread StartBackgroundUpdateDownload(string baseDir)
    {
        var thread = new Thread(() =>
        {
            UpdateNotification? notification = RunExclusiveUpdateDownload(baseDir);
            if (notification != null)
            {
                Thread notificationThread = ShowTimedMessageAsync(
                    notification.Message,
                    notification.IsError ? ErrorIcon : InfoIcon);
                notificationThread.Join();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = false;
        thread.Start();
        return thread;
    }

    private static UpdateNotification? RunExclusiveUpdateDownload(string baseDir)
    {
        return RunExclusiveUpdateWork("后台下载", () =>
        {
            using var cts = new CancellationTokenSource(NetworkUpdateTimeout);
            return CheckAndDownloadPatchUpdateAsync(baseDir, cts.Token).GetAwaiter().GetResult();
        });
    }

    private static UpdateNotification? RunExclusiveUpdateWork(
        string operationName,
        Func<UpdateNotification?> work)
    {
        bool hasLock = false;

        try
        {
            using var mutex = new Mutex(initiallyOwned: false, UpdateMutexName);
            try
            {
                try
                {
                    hasLock = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    hasLock = true;
                }

                if (!hasLock)
                {
                    WriteLog($"检测到另一个启动器正在处理更新，跳过本次{operationName}");
                    return null;
                }

                return work();
            }
            finally
            {
                if (hasLock)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog($"启动器更新{operationName}异常：" + ex);
            return null;
        }
    }

    private static UpdateNotification? InstallPendingUpdate(string baseDir)
    {
        string pendingDir = GetPendingUpdateDir();
        string manifestPath = Path.Combine(pendingDir, "update_manifest.json");
        if (!File.Exists(manifestPath))
            return null;

        string patchZipPath = FindPendingPatchZip(pendingDir);
        if (string.IsNullOrWhiteSpace(patchZipPath))
        {
            WriteLog("pending 更新缺少 Patch zip，已清理 pending");
            TryDeleteDirectory(pendingDir);
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            UpdateDescriptor descriptor = ReadUpdateDescriptor(document.RootElement, "");
            ValidatePatchDescriptor(descriptor);
            string currentVersion = ReadInstalledAppVersion(baseDir);
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                WriteLog("无法读取当前版本，跳过 pending 更新安装");
                return null;
            }

            if (CompareVersions(currentVersion, descriptor.LatestVersion) >= 0)
            {
                WriteLog($"当前版本 {currentVersion} 已不低于 pending 版本 {descriptor.LatestVersion}，清理 pending 更新");
                TryDeleteDirectory(pendingDir);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.PatchBaselineVersion) &&
                CompareVersions(currentVersion, descriptor.PatchBaselineVersion) < 0)
            {
                WriteLog($"当前版本 {currentVersion} 低于 pending Patch 基线 {descriptor.PatchBaselineVersion}，清理 pending 更新");
                TryDeleteDirectory(pendingDir);
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.VersionBelowBaseline);
            }

            ValidateDownloadedFileSize(patchZipPath, descriptor.PatchPackage.Size);
            string actualHash = ComputeSha256(patchZipPath);
            if (!string.Equals(actualHash, descriptor.PatchPackage.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("pending Patch 包 SHA256 校验失败");

            InstallPatchZip(baseDir, patchZipPath, descriptor);
            TryDeleteDirectory(pendingDir);
            ResetPatchDownloadFailureState();
            WriteLog($"pending Patch 更新完成：{descriptor.LatestVersion}");
            return new UpdateNotification(BuildSuccessMessage(descriptor), false);
        }
        catch (Exception ex)
        {
            WriteLog("pending Patch 更新失败：" + ex);

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                UpdateDescriptor descriptor = ReadUpdateDescriptor(document.RootElement, "");
                return new UpdateNotification(BuildFailedMessage(descriptor), true);
            }
            catch
            {
                return null;
            }
            finally
            {
                TryDeleteDirectory(pendingDir);
            }
        }
    }

    private static async Task<UpdateNotification?> CheckAndDownloadPatchUpdateAsync(
        string baseDir,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ReadAutoCheckEnabled())
                return null;

            string currentVersion = ReadInstalledAppVersion(baseDir);
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                WriteLog("无法读取当前版本，跳过自动更新检查");
                return null;
            }

            UpdateCheckUrlInfo checkUrl = GetUpdateCheckUrl(baseDir);
            if (string.IsNullOrWhiteSpace(checkUrl.Url))
                return null;

            WriteLog("自动检查更新地址来源：" + checkUrl.Source);
            using JsonDocument release = await GetJsonAsync(checkUrl.Url, cancellationToken);
            JsonElement releaseRoot = release.RootElement;
            string tagName = ReadString(releaseRoot, "tag_name");
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            string latestVersion = NormalizeVersion(tagName);
            if (CompareVersions(latestVersion, currentVersion) <= 0)
                return null;

            AssetInfo? manifestAsset = FindUpdateManifestAsset(releaseRoot, latestVersion);
            if (manifestAsset == null)
                return null;

            using JsonDocument updateManifest = await GetJsonAsync(manifestAsset.Url, cancellationToken);
            UpdateDescriptor descriptor = ReadUpdateDescriptor(updateManifest.RootElement, latestVersion);

            if (!descriptor.PatchSupported)
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.PatchNotSupported);

            if (!string.IsNullOrWhiteSpace(descriptor.PatchBaselineVersion) &&
                CompareVersions(currentVersion, descriptor.PatchBaselineVersion) < 0)
            {
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.VersionBelowBaseline);
            }

            if (!IsPatchDescriptorUsable(descriptor))
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.PatchDescriptorUnavailable);

            await DownloadPendingPatchAsync(updateManifest.RootElement, descriptor, cancellationToken);
            WriteLog($"Patch 已下载到 pending，下次启动安装：{descriptor.LatestVersion}");
            return null;
        }
        catch (OperationCanceledException ex)
        {
            WriteLog("自动检查更新超时：" + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            WriteLog("自动检查更新失败：" + ex);
            return null;
        }
    }

    private static async Task DownloadPendingPatchAsync(
        JsonElement manifestRoot,
        UpdateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        string updatesDir = GetUpdatesCacheDir();
        string pendingDir = GetPendingUpdateDir();
        string downloadDir = Path.Combine(updatesDir, "download");
        string patchZipName = GetPatchZipFileName(descriptor);
        string downloadPath = Path.Combine(downloadDir, patchZipName);
        string pendingPatchPath = Path.Combine(pendingDir, patchZipName);
        string pendingManifestPath = Path.Combine(pendingDir, "update_manifest.json");
        string tmpPath = downloadPath + ".tmp";

        try
        {
            Directory.CreateDirectory(updatesDir);
            SafeDeleteDirectory(downloadDir);
            Directory.CreateDirectory(downloadDir);
            DeleteFileIfExists(tmpPath);

            string githubUrl = BuildDefaultGithubPatchDownloadUrl(descriptor, patchZipName);
            string fallbackUrl = descriptor.PatchPackage.Url;
            PatchDownloadFailureState failureState = LoadPatchDownloadFailureState(descriptor.LatestVersion);
            bool preferFallback = failureState.ConsecutiveGithubDownloadFailures >= GithubDownloadFailureFallbackThreshold &&
                !AreSameUrl(githubUrl, fallbackUrl);
            string selectedUrl = preferFallback ? fallbackUrl : githubUrl;

            try
            {
                WriteLog($"准备下载 Patch：version={descriptor.LatestVersion}, source={(preferFallback ? "manifest" : "github")}, failures={failureState.ConsecutiveGithubDownloadFailures}, url={selectedUrl}");
                await DownloadFileAsync(selectedUrl, tmpPath, cancellationToken);
            }
            catch (Exception ex) when (!preferFallback && !AreSameUrl(githubUrl, fallbackUrl))
            {
                failureState.ConsecutiveGithubDownloadFailures++;
                SavePatchDownloadFailureState(failureState);
                WriteLog($"GitHub Patch 下载失败 {failureState.ConsecutiveGithubDownloadFailures}/{GithubDownloadFailureFallbackThreshold}：{ex.Message}");

                if (failureState.ConsecutiveGithubDownloadFailures < GithubDownloadFailureFallbackThreshold)
                    throw;

                TryDeleteFile(tmpPath);
                WriteLog($"GitHub Patch 连续失败达到阈值，改用更新描述中的下载地址：{fallbackUrl}");
                await DownloadFileAsync(fallbackUrl, tmpPath, cancellationToken);
            }

            ValidateDownloadedFileSize(tmpPath, descriptor.PatchPackage.Size);
            string actualHash = ComputeSha256(tmpPath);
            if (!string.Equals(actualHash, descriptor.PatchPackage.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Patch 包 SHA256 校验失败");

            SafeDeleteDirectory(pendingDir);
            Directory.CreateDirectory(pendingDir);
            File.Move(tmpPath, pendingPatchPath);
            File.WriteAllText(pendingManifestPath, manifestRoot.GetRawText(), Encoding.UTF8);
            ResetPatchDownloadFailureState();
        }
        finally
        {
            TryDeleteFile(tmpPath);
            TryDeleteDirectory(downloadDir);
        }
    }

    private static void InstallPatchZip(string baseDir, string patchZipPath, UpdateDescriptor descriptor)
    {
        string stagingDir = Path.Combine(GetUpdatesCacheDir(), "staging");
        SafeDeleteDirectory(stagingDir);

        try
        {
            ZipFile.ExtractToDirectory(patchZipPath, stagingDir, overwriteFiles: true);
            InstallPatchFromStaging(baseDir, stagingDir, descriptor);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private static void InstallPatchFromStaging(string baseDir, string stagingDir, UpdateDescriptor descriptor)
    {
        string manifestPath = Path.Combine(stagingDir, "patch_manifest.json");
        string patchFilesDir = Path.Combine(stagingDir, "files");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Patch 包缺少 patch_manifest.json");
        if (!Directory.Exists(patchFilesDir))
            throw new InvalidOperationException("Patch 包缺少 files 目录");

        PatchManifest manifest = ReadPatchManifest(manifestPath);
        ValidatePatchManifest(manifest, descriptor);
        ValidatePatchFiles(patchFilesDir, manifest.Files);

        string appDir = Path.Combine(baseDir, "app");
        string backupRoot = GetUpdateBackupDir();
        SafeDeleteDirectory(backupRoot);
        Directory.CreateDirectory(backupRoot);

        var backups = new List<FileBackup>();
        try
        {
            foreach (PatchFile file in manifest.Files)
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

            foreach (PatchFile file in manifest.Files)
            {
                string sourcePath = GetSafeChildPath(patchFilesDir, file.RelativePath);
                string targetPath = GetSafeChildPath(appDir, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            ValidateInstalledApp(baseDir, descriptor.LatestVersion);
            TryDeleteDirectory(backupRoot);
        }
        catch
        {
            RollbackFiles(backups);
            throw;
        }
    }

    private static PatchManifest ReadPatchManifest(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Patch manifest 缺少 files 列表");

        var files = new List<PatchFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement fileElement in filesElement.EnumerateArray())
        {
            string relativePath = NormalizeRelativePath(ReadString(fileElement, "path"));
            string sha256 = ReadString(fileElement, "sha256");
            long size = ReadInt64(fileElement, "size");
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(sha256))
                throw new InvalidOperationException("Patch manifest 文件条目不完整");
            if (!seenPaths.Add(relativePath))
                throw new InvalidOperationException($"Patch manifest 文件重复：{relativePath}");

            files.Add(new PatchFile(relativePath, sha256, size));
        }

        return new PatchManifest(
            ReadString(root, "type"),
            NormalizeVersion(ReadString(root, "patch_baseline_version")),
            NormalizeVersion(ReadString(root, "latest_version")),
            files);
    }

    private static void ValidatePatchManifest(PatchManifest manifest, UpdateDescriptor descriptor)
    {
        if (!string.Equals(manifest.Type, PatchPackageType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch manifest 类型不支持");

        if (!string.Equals(manifest.LatestVersion, descriptor.LatestVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch manifest 最新版本与更新描述不一致");

        if (!string.IsNullOrWhiteSpace(descriptor.PatchBaselineVersion) &&
            !string.Equals(manifest.PatchBaselineVersion, descriptor.PatchBaselineVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Patch manifest 基线版本与更新描述不一致");
        }
    }

    private static void ValidatePatchDescriptor(UpdateDescriptor descriptor)
    {
        if (!IsPatchDescriptorUsable(descriptor))
            throw new InvalidOperationException("更新描述中的 Patch 信息不完整或不支持");
    }

    private static bool IsPatchDescriptorUsable(UpdateDescriptor descriptor)
    {
        return descriptor.PatchSupported &&
            string.Equals(descriptor.PatchPackage.Type, PatchPackageType, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(descriptor.PatchPackage.Url) &&
            !string.IsNullOrWhiteSpace(descriptor.PatchPackage.Sha256) &&
            !string.IsNullOrWhiteSpace(descriptor.LatestVersion);
    }

    private static void ValidatePatchFiles(string patchFilesDir, List<PatchFile> files)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("Patch manifest 文件列表为空");

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

    private static void ValidateDownloadedFileSize(string path, long expectedSize)
    {
        if (expectedSize <= 0)
            return;

        long actualSize = new FileInfo(path).Length;
        if (actualSize != expectedSize)
            throw new InvalidOperationException("Patch 包大小校验失败");
    }

    private static void ValidateInstalledApp(string baseDir, string expectedVersion)
    {
        string appPath = Path.Combine(baseDir, AppRelativePath);
        string dllPath = Path.Combine(baseDir, AppDllRelativePath);
        if (!File.Exists(appPath))
            throw new InvalidOperationException("更新后缺少主程序 exe");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException("更新后缺少主程序 dll");

        string installedVersion = ReadInstalledAppVersion(baseDir);
        if (string.IsNullOrWhiteSpace(installedVersion) ||
            CompareVersions(installedVersion, expectedVersion) != 0)
        {
            throw new InvalidOperationException($"更新后版本校验失败：{installedVersion} != {expectedVersion}");
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
            ShowMessage($"未找到主程序：{AppRelativePath}\n\n请确认 app 文件夹与本启动程序放在同一目录。", ErrorIcon);
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
            ShowMessage($"启动主程序失败：\n{ex.Message}", ErrorIcon);
            return 1;
        }
    }

    private static bool IsAppRunning(string appPath)
    {
        string expectedPath = Path.GetFullPath(appPath);
        foreach (Process process in Process.GetProcessesByName("ExpressPackingMonitoring"))
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                string? processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath) &&
                    string.Equals(Path.GetFullPath(processPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static bool TryActivateExistingApp(string[] args)
    {
        string requestedRole = ResolveRequestedRole(args);
        if (IsKnownRole(requestedRole) &&
            IsRoleRunning(requestedRole) &&
            RequestActivate(requestedRole))
        {
            return true;
        }

        string configuredRole = ReadConfiguredWorkstationRole();
        if (IsKnownRole(configuredRole) &&
            IsRoleRunning(configuredRole) &&
            RequestActivate(configuredRole))
        {
            return true;
        }

        foreach (string role in new[] { CameraMonitorRole, PrintStationRole })
        {
            if (IsRoleRunning(role) && RequestActivate(role))
                return true;
        }

        return false;
    }

    private static string ResolveRequestedRole(string[] args)
    {
        string temporaryRole = ResolveRoleOption(args, "--temporary-role");
        if (IsKnownRole(temporaryRole))
            return temporaryRole;

        string requestedRole = ResolveRoleOption(args, "--role");
        if (IsKnownRole(requestedRole))
            return requestedRole;

        if (args.Any(a => string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase)))
            return CameraMonitorRole;

        if (args.Any(a => string.Equals(a, "--order-workstation", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(a, "--print-station", StringComparison.OrdinalIgnoreCase)))
        {
            return PrintStationRole;
        }

        return "";
    }

    private static string ResolveRoleOption(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i] ?? "";
            if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                return NormalizeRoleName(arg[(optionName.Length + 1)..]);
            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return NormalizeRoleName(args[i + 1]);
        }

        return "";
    }

    private static string ReadConfiguredWorkstationRole()
    {
        string path = Path.Combine(GetUserDataDir(), "config.json");
        if (!File.Exists(path))
            return "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return NormalizeRoleName(ReadString(document.RootElement, "WorkstationRole"));
        }
        catch (Exception ex)
        {
            WriteLog("读取工位配置失败：" + ex.Message);
            return "";
        }
    }

    private static bool IsRoleRunning(string role)
    {
        if (!IsKnownRole(role))
            return false;

        try
        {
            using var existing = Mutex.OpenExisting(GetRoleMutexName(role));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RequestActivate(string role)
    {
        if (!IsKnownRole(role))
            return false;

        try
        {
            using var pipe = new NamedPipeClientStream(".", GetRolePipeName(role), PipeDirection.Out);
            pipe.Connect(400);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using Stream target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
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

    private static UpdateDescriptor ReadUpdateDescriptor(JsonElement manifestRoot, string fallbackVersion)
    {
        string latestVersion = NormalizeVersion(ReadString(manifestRoot, "latest_version"));
        if (string.IsNullOrWhiteSpace(latestVersion))
            latestVersion = fallbackVersion;

        string fullDownloadPage = ReadString(manifestRoot, "full_download_page");
        if (string.IsNullOrWhiteSpace(fullDownloadPage))
            fullDownloadPage = ReadString(manifestRoot, "release_page");

        return new UpdateDescriptor(
            latestVersion,
            ReadString(manifestRoot, "title"),
            ReadNotes(manifestRoot),
            fullDownloadPage,
            NormalizeVersion(ReadString(manifestRoot, "patch_baseline_version")),
            ReadBoolean(manifestRoot, "patch_supported"),
            ReadPatchPackageInfo(manifestRoot));
    }

    private static PatchPackageInfo ReadPatchPackageInfo(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("patch_package", out JsonElement package) || package.ValueKind != JsonValueKind.Object)
            return new PatchPackageInfo("", "", "", -1);

        return new PatchPackageInfo(
            ReadString(package, "type"),
            ReadString(package, "url"),
            ReadString(package, "sha256"),
            ReadInt64(package, "size"));
    }

    private static string[] ReadNotes(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("notes", out JsonElement notes) || notes.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return notes
            .EnumerateArray()
            .Where(note => note.ValueKind == JsonValueKind.String)
            .Select(note => note.GetString() ?? "")
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .ToArray();
    }

    private static UpdateNotification BuildManualUpdateNotification(UpdateDescriptor descriptor, ManualUpdateReason reason)
    {
        string reasonText = reason switch
        {
            ManualUpdateReason.PatchNotSupported => "本次包含启动器或基础组件更新，需要下载完整包后解压覆盖安装。",
            ManualUpdateReason.VersionBelowBaseline => "当前版本过旧，不能直接使用本次增量更新，需要下载完整包后解压覆盖安装。",
            ManualUpdateReason.PatchDescriptorUnavailable => "本版本需要完整更新，需要下载完整包后解压覆盖安装。",
            _ => "本版本需要完整更新，需要下载完整包后解压覆盖安装。"
        };

        string message = $"发现新版本：v{descriptor.LatestVersion}\n{reasonText}";
        if (!string.IsNullOrWhiteSpace(descriptor.FullDownloadPage))
            message += $"\n完整包下载页：{descriptor.FullDownloadPage}";

        return new UpdateNotification(message, true);
    }

    private static string BuildSuccessMessage(UpdateDescriptor descriptor)
    {
        var lines = new List<string>
        {
            $"更新完成，已升级到 v{descriptor.LatestVersion}。"
        };

        if (!string.IsNullOrWhiteSpace(descriptor.Title))
        {
            lines.Add("");
            lines.Add(descriptor.Title);
        }

        if (descriptor.Notes.Length > 0)
        {
            lines.Add("");
            lines.AddRange(descriptor.Notes.Select(note => "- " + note));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFailedMessage(UpdateDescriptor descriptor)
    {
        string message = $"增量更新失败，已恢复旧版本。\n\n发现新版本：v{descriptor.LatestVersion}\n请下载完整包后解压覆盖安装。";
        if (!string.IsNullOrWhiteSpace(descriptor.FullDownloadPage))
            message += $"\n完整包下载页：{descriptor.FullDownloadPage}";

        return message;
    }

    private static bool ReadAutoCheckEnabled()
    {
        string path = Path.Combine(GetUserDataDir(), "config.json");
        if (!File.Exists(path))
            return true;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.TryGetProperty("EnableAutoCheckUpdate", out JsonElement value) &&
                (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                return value.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            WriteLog("读取自动更新配置失败：" + ex.Message);
        }

        return true;
    }

    private static string ReadInstalledAppVersion(string baseDir)
    {
        string dllPath = Path.Combine(baseDir, AppDllRelativePath);
        if (!File.Exists(dllPath))
            return "";

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(dllPath);
            string version = info.ProductVersion ?? "";
            return NormalizeVersion(version);
        }
        catch (Exception ex)
        {
            WriteLog("读取当前版本失败：" + ex.Message);
            return "";
        }
    }

    private static UpdateCheckUrlInfo GetUpdateCheckUrl(string baseDir)
    {
        string? value = Environment.GetEnvironmentVariable(UpdateUrlKey);
        if (!string.IsNullOrWhiteSpace(value))
            return new UpdateCheckUrlInfo(value.Trim(), "环境变量");

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
                    return new UpdateCheckUrlInfo(line[(separator + 1)..].Trim().Trim('"', '\''), ".env");
            }
        }

        return GetEmbeddedDefaultCheckUrl();
    }

    private static UpdateCheckUrlInfo GetEmbeddedDefaultCheckUrl()
    {
        try
        {
            foreach (AssemblyMetadataAttribute metadata in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(metadata.Key, DefaultCheckUrlMetadataKey, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(metadata.Value))
                {
                    return new UpdateCheckUrlInfo(metadata.Value.Trim(), "内置配置");
                }
            }
        }
        catch
        {
        }

        return new UpdateCheckUrlInfo(FallbackCheckUrl, "默认配置");
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

        return value.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return 0;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : 0;
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
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        int metadataIndex = normalized.IndexOf('+');
        return metadataIndex > 0 ? normalized[..metadataIndex] : normalized;
    }

    private static string EscapeJsonString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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

    private static string GetUserDataDir()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData;
        return Path.Combine(root, "ExpressPackingMonitoring");
    }

    private static string GetUpdatesCacheDir()
    {
        return Path.Combine(GetUserDataDir(), "cache", "updates");
    }

    private static string GetPendingUpdateDir()
    {
        return Path.Combine(GetUpdatesCacheDir(), "pending");
    }

    private static bool HasPendingUpdate()
    {
        string pendingDir = GetPendingUpdateDir();
        return File.Exists(Path.Combine(pendingDir, "update_manifest.json")) &&
            !string.IsNullOrWhiteSpace(FindPendingPatchZip(pendingDir));
    }

    private static string GetUpdateBackupDir()
    {
        return Path.Combine(GetUserDataDir(), "backups", "launcher-update");
    }

    private static string GetLogPath()
    {
        return Path.Combine(GetUserDataDir(), "log", "launcher_update.log");
    }

    private static string GetPatchDownloadFailureStatePath()
    {
        return Path.Combine(GetUpdatesCacheDir(), "patch_download_failures.json");
    }

    private static string FindPendingPatchZip(string pendingDir)
    {
        if (!Directory.Exists(pendingDir))
            return "";

        return Directory
            .EnumerateFiles(pendingDir, "ExpressPackingMonitoring_AppPatch_v*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? "";
    }

    private static string GetPatchZipFileName(UpdateDescriptor descriptor)
    {
        try
        {
            string fileName = Path.GetFileName(new Uri(descriptor.PatchPackage.Url).LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName) &&
                fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }
        }
        catch
        {
        }

        return $"ExpressPackingMonitoring_AppPatch_v{descriptor.LatestVersion}.zip";
    }

    private static string BuildDefaultGithubPatchDownloadUrl(UpdateDescriptor descriptor, string patchZipName)
    {
        string tag = "v" + NormalizeVersion(descriptor.LatestVersion);
        return $"{DefaultPatchDownloadBaseUrl}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(patchZipName)}";
    }

    private static PatchDownloadFailureState LoadPatchDownloadFailureState(string latestVersion)
    {
        string normalizedVersion = NormalizeVersion(latestVersion);
        string path = GetPatchDownloadFailureStatePath();
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement root = document.RootElement;
                string stateVersion = NormalizeVersion(ReadString(root, "latest_version"));
                int failures = ReadInt32(root, "consecutive_github_download_failures");
                if (string.Equals(stateVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return new PatchDownloadFailureState
                    {
                        LatestVersion = normalizedVersion,
                        ConsecutiveGithubDownloadFailures = Math.Max(0, failures)
                    };
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("读取 Patch 下载失败计数失败：" + ex.Message);
        }

        return new PatchDownloadFailureState
        {
            LatestVersion = normalizedVersion,
            ConsecutiveGithubDownloadFailures = 0
        };
    }

    private static void SavePatchDownloadFailureState(PatchDownloadFailureState state)
    {
        try
        {
            string path = GetPatchDownloadFailureStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = "{" +
                $"\"latest_version\":\"{EscapeJsonString(NormalizeVersion(state.LatestVersion))}\"," +
                $"\"consecutive_github_download_failures\":{Math.Max(0, state.ConsecutiveGithubDownloadFailures)}" +
                "}";
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            WriteLog("写入 Patch 下载失败计数失败：" + ex.Message);
        }
    }

    private static void ResetPatchDownloadFailureState()
    {
        TryDeleteFile(GetPatchDownloadFailureStatePath());
    }

    private static bool AreSameUrl(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownRole(string role)
    {
        return string.Equals(role, CameraMonitorRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, PrintStationRole, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoleName(string? role)
    {
        if (string.Equals(role, CameraMonitorRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "monitor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "camera", StringComparison.OrdinalIgnoreCase))
        {
            return CameraMonitorRole;
        }

        if (string.Equals(role, PrintStationRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "print", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "printer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "order", StringComparison.OrdinalIgnoreCase))
        {
            return PrintStationRole;
        }

        return "";
    }

    private static string NormalizeRole(string role)
    {
        return string.Equals(role, PrintStationRole, StringComparison.OrdinalIgnoreCase)
            ? PrintStationRole
            : CameraMonitorRole;
    }

    private static string GetRoleMutexName(string role)
    {
        return $@"Local\{InstanceNamePrefix}.{NormalizeRole(role)}.Mutex";
    }

    private static string GetRolePipeName(string role)
    {
        return $"{InstanceNamePrefix}.{NormalizeRole(role)}.Activate";
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            SafeDeleteDirectory(path);
        }
        catch (Exception ex)
        {
            WriteLog("清理目录失败：" + path + Environment.NewLine + ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            DeleteFileIfExists(path);
        }
        catch (Exception ex)
        {
            WriteLog("清理文件失败：" + path + Environment.NewLine + ex);
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            string logPath = GetLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static Thread ShowTimedMessageAsync(string message, uint icon)
    {
        var thread = new Thread(() => ShowTimedMessage(message, icon));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = false;
        thread.Start();
        return thread;
    }

    private static void ShowTimedMessage(string message, uint icon)
    {
        try
        {
            MessageBoxTimeoutW(IntPtr.Zero, message, "打包监控", icon, 0, DialogTimeoutMs);
        }
        catch
        {
            ShowMessage(message, icon);
        }
    }

    private static void ShowMessage(string message, uint icon)
    {
        MessageBoxW(IntPtr.Zero, message, "打包监控", icon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeoutW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType,
        ushort wLanguageId,
        uint dwMilliseconds);

    private sealed record UpdateCheckUrlInfo(string Url, string Source);

    private sealed record AssetInfo(string Name, string Url);

    private sealed record PatchPackageInfo(string Type, string Url, string Sha256, long Size);

    private sealed record UpdateDescriptor(
        string LatestVersion,
        string Title,
        string[] Notes,
        string FullDownloadPage,
        string PatchBaselineVersion,
        bool PatchSupported,
        PatchPackageInfo PatchPackage);

    private sealed record UpdateNotification(string Message, bool IsError);

    private sealed record PatchManifest(
        string Type,
        string PatchBaselineVersion,
        string LatestVersion,
        List<PatchFile> Files);

    private sealed record PatchFile(string RelativePath, string Sha256, long Size);

    private sealed record FileBackup(string TargetPath, string BackupPath, bool Existed);

    private enum ManualUpdateReason
    {
        PatchNotSupported,
        VersionBelowBaseline,
        PatchDescriptorUnavailable
    }

    private sealed class PatchDownloadFailureState
    {
        public string LatestVersion { get; set; } = "";
        public int ConsecutiveGithubDownloadFailures { get; set; }
    }
}
