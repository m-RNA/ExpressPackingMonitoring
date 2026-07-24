using System.Diagnostics;
using System.Security.Cryptography;
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

    private sealed class ManualPatchFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _patchRoot;
        private readonly string _configPath;
        private readonly string _scriptPath;

        public string TargetFilePath { get; }

        public ManualPatchFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-manual-patch-tests", Guid.NewGuid().ToString("N"));
            _patchRoot = Path.Combine(_root, "patch");
            string appRoot = Path.Combine(_root, "installed", "app");
            string userDataRoot = Path.Combine(_root, "user-data");
            _configPath = Path.Combine(userDataRoot, "config.json");
            TargetFilePath = Path.Combine(appRoot, "Web", "index.html");
            _scriptPath = Path.Combine(FindRepositoryRoot(), "Tools", "Apply-AppPatch.ps1");

            Directory.CreateDirectory(Path.GetDirectoryName(TargetFilePath)!);
            Directory.CreateDirectory(userDataRoot);
            File.WriteAllText(Path.Combine(appRoot, "ExpressPackingMonitoring.exe"), "test", Encoding.UTF8);
            File.WriteAllText(Path.Combine(appRoot, "ExpressPackingMonitoring.dll"), "test", Encoding.UTF8);
            File.WriteAllText(TargetFilePath, "old-content", Encoding.UTF8);
            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(new { AppRootDirectory = appRoot }),
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

        public async Task<ProcessResult> RunInstallerAsync()
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
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["EPM_TEST_PATCH_SCRIPT"] = _scriptPath;
            startInfo.Environment["EPM_TEST_PATCH_ROOT"] = _patchRoot;
            startInfo.Environment["EPM_TEST_CONFIG_PATH"] = _configPath;
            foreach (string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-Command",
                "$scriptText=[System.IO.File]::ReadAllText($env:EPM_TEST_PATCH_SCRIPT,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $env:EPM_TEST_PATCH_ROOT -ConfigPath $env:EPM_TEST_CONFIG_PATH -SkipProcessCheck -SkipVersionCheck"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell");
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
