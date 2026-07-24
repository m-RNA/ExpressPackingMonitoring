using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ManualPatchInstallerTests
{
    [Fact]
    public async Task Installer_UsesConfiguredAppRootAndAppliesVerifiedFiles()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("增量更新完成", result.StandardOutput);
    }

    [Fact]
    public async Task Installer_RejectsHashMismatchWithoutChangingInstalledFile()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("tampered-content", useValidHash: false);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("SHA256 校验失败", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public async Task Installer_RollsBackFilesWhenReplacementFailsMidway()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePartiallyFailingPatch();

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("正在恢复原文件", result.StandardOutput);
    }

    [Theory]
    [InlineData("missing", "package-root")]
    [InlineData("invalid", "app-directory")]
    [InlineData("legacy", "launcher-exe")]
    [InlineData("stale", "app-exe")]
    [InlineData("missing", "quoted-package-root")]
    [InlineData("missing", "legacy-flat-root")]
    [InlineData("missing", "shortcut-package-root")]
    [InlineData("missing", "shortcut-app-exe")]
    public async Task Installer_AcceptsDraggedInstallLocationsWhenConfigCannotBeUsed(
        string configState,
        string candidateKind)
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.SetConfigState(configState);

        ProcessResult result = await fixture.RunInstallerAsync(fixture.GetManualCandidate(candidateKind));

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    [Theory]
    [InlineData("missing-target-shortcut", "路径不存在")]
    [InlineData("internet-shortcut", "不支持网络快捷方式")]
    public async Task Installer_RejectsInvalidShortcutInputs(string candidateKind, string expectedMessage)
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.SetConfigState("missing");

        ProcessResult result = await fixture.RunInstallerAsync(fixture.GetManualCandidate(candidateKind));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, result.StandardOutput + result.StandardError);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    [Fact]
    public async Task Installer_PromptsForDraggedLocationWhenAutomaticDetectionFails()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.SetConfigState("missing");

        ProcessResult result = await fixture.RunInstallerAsync(
            standardInput: fixture.GetManualCandidate("quoted-package-root"));

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Contains("请把安装文件夹或 ExpressPackingMonitoring.exe 拖到此窗口", result.StandardOutput);
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    private sealed class ManualPatchFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _patchRoot;
        private readonly string _configPath;
        private readonly string _scriptPath;
        private readonly string _installedRoot;
        private readonly string _appRoot;

        public string TargetFilePath { get; private set; }

        public ManualPatchFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-manual-patch-tests", Guid.NewGuid().ToString("N"));
            _patchRoot = Path.Combine(_root, "patch");
            _installedRoot = Path.Combine(_root, "installed package");
            _appRoot = Path.Combine(_installedRoot, "app");
            string userDataRoot = Path.Combine(_root, "user-data");
            _configPath = Path.Combine(userDataRoot, "config.json");
            TargetFilePath = Path.Combine(_appRoot, "Web", "index.html");
            _scriptPath = Path.Combine(FindRepositoryRoot(), "Tools", "Apply-AppPatch.ps1");

            Directory.CreateDirectory(Path.GetDirectoryName(TargetFilePath)!);
            Directory.CreateDirectory(userDataRoot);
            File.WriteAllText(Path.Combine(_installedRoot, "ExpressPackingMonitoring.exe"), "launcher", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_appRoot, "ExpressPackingMonitoring.exe"), "test", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_appRoot, "ExpressPackingMonitoring.dll"), "test", Encoding.UTF8);
            File.WriteAllText(TargetFilePath, "old-content", Encoding.UTF8);
            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(new { AppRootDirectory = _appRoot }),
                Encoding.UTF8);
        }

        public void CreatePatch(string content, bool useValidHash)
        {
            string sourcePath = Path.Combine(_patchRoot, "files", "Web", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, content, Encoding.UTF8);
            byte[] bytes = File.ReadAllBytes(sourcePath);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!useValidHash)
                hash = new string('0', 64);

            WriteManifest(
            [
                new
                {
                    path = "Web/index.html",
                    sha256 = hash,
                    size = bytes.LongLength
                }
            ]);
        }

        public void CreatePartiallyFailingPatch()
        {
            string firstSource = Path.Combine(_patchRoot, "files", "Web", "index.html");
            string blockedSource = Path.Combine(_patchRoot, "files", "blocked");
            Directory.CreateDirectory(Path.GetDirectoryName(firstSource)!);
            File.WriteAllText(firstSource, "new-content", Encoding.UTF8);
            File.WriteAllText(blockedSource, "blocked-content", Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TargetFilePath)!)!, "blocked"));

            byte[] firstBytes = File.ReadAllBytes(firstSource);
            byte[] blockedBytes = File.ReadAllBytes(blockedSource);
            WriteManifest(
            [
                new
                {
                    path = "Web/index.html",
                    sha256 = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant(),
                    size = firstBytes.LongLength
                },
                new
                {
                    path = "blocked",
                    sha256 = Convert.ToHexString(SHA256.HashData(blockedBytes)).ToLowerInvariant(),
                    size = blockedBytes.LongLength
                }
            ]);
        }

        public void SetConfigState(string state)
        {
            switch (state)
            {
                case "missing":
                    File.Delete(_configPath);
                    break;
                case "invalid":
                    File.WriteAllText(_configPath, "{invalid-json", Encoding.UTF8);
                    break;
                case "legacy":
                    File.WriteAllText(_configPath, "{}", Encoding.UTF8);
                    break;
                case "stale":
                    File.WriteAllText(
                        _configPath,
                        JsonSerializer.Serialize(new { AppRootDirectory = Path.Combine(_root, "moved-app") }),
                        Encoding.UTF8);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        public string GetManualCandidate(string kind)
        {
            return kind switch
            {
                "package-root" => _installedRoot,
                "app-directory" => _appRoot,
                "launcher-exe" => Path.Combine(_installedRoot, "ExpressPackingMonitoring.exe"),
                "app-exe" => Path.Combine(_appRoot, "ExpressPackingMonitoring.exe"),
                "quoted-package-root" => $"\"{_installedRoot}\"",
                "legacy-flat-root" => PrepareLegacyFlatRoot(),
                "shortcut-package-root" => CreateShortcut("package-root.lnk", _installedRoot),
                "shortcut-app-exe" => CreateShortcut(
                    "app-exe.lnk",
                    Path.Combine(_appRoot, "ExpressPackingMonitoring.exe")),
                "missing-target-shortcut" => CreateShortcut(
                    "missing-target.lnk",
                    Path.Combine(_root, "missing", "ExpressPackingMonitoring.exe")),
                "internet-shortcut" => CreateInternetShortcut(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private string PrepareLegacyFlatRoot()
        {
            string flatTargetPath = Path.Combine(_installedRoot, "Web", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(flatTargetPath)!);
            File.WriteAllText(Path.Combine(_installedRoot, "ExpressPackingMonitoring.dll"), "test", Encoding.UTF8);
            File.WriteAllText(flatTargetPath, "old-content", Encoding.UTF8);
            TargetFilePath = flatTargetPath;
            return _installedRoot;
        }

        private string CreateShortcut(string fileName, string targetPath)
        {
            string shortcutPath = Path.Combine(_root, fileName);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable");
            object shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Unable to create WScript.Shell");
            object? shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    [shortcutPath]);
                Type shortcutType = shortcut!.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    [targetPath]);
                shortcutType.InvokeMember(
                    "Save",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
                if (Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
            return shortcutPath;
        }

        private string CreateInternetShortcut()
        {
            string path = Path.Combine(_root, "website.url");
            File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/\r\n", Encoding.UTF8);
            return path;
        }

        private void WriteManifest(object[] files)
        {
            var manifest = new
            {
                type = "baseline_patch",
                patch_baseline_version = "0.0.1",
                latest_version = "0.0.2",
                files
            };
            Directory.CreateDirectory(_patchRoot);
            File.WriteAllText(
                Path.Combine(_patchRoot, "patch_manifest.json"),
                JsonSerializer.Serialize(manifest),
                Encoding.UTF8);
        }

        public async Task<ProcessResult> RunInstallerAsync(
            string appRootPath = "",
            string? standardInput = null)
        {
            string powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["EPM_TEST_PATCH_SCRIPT"] = _scriptPath;
            startInfo.Environment["EPM_TEST_PATCH_ROOT"] = _patchRoot;
            startInfo.Environment["EPM_TEST_CONFIG_PATH"] = _configPath;
            startInfo.Environment["EPM_TEST_APP_ROOT_PATH"] = appRootPath;
            foreach (string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-Command",
                "$scriptText=[System.IO.File]::ReadAllText($env:EPM_TEST_PATCH_SCRIPT,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $env:EPM_TEST_PATCH_ROOT -ConfigPath $env:EPM_TEST_CONFIG_PATH -AppRootPath $env:EPM_TEST_APP_ROOT_PATH -SkipProcessCheck -SkipVersionCheck"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell");
            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput);
                process.StandardInput.Close();
            }
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
