using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ReleasePackagingPolicyTests
{
    [Fact]
    public void Packaging_WarnsButDoesNotBlockWhenManualChecksAreUnconfirmed()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string incrementalScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "打包脚本-增量.bat"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "打包脚本-基线.bat"),
            Encoding.UTF8);

        Assert.Contains("Packaging will continue", publishScript);
        Assert.DoesNotContain("throw \"Manual core business", publishScript);
        Assert.DoesNotContain("choice /C YN", incrementalScript);
        Assert.DoesNotContain("-ConfirmManualCoreChecks", incrementalScript);
        Assert.DoesNotContain("choice /C YN", baselineScript);
        Assert.DoesNotContain("-ConfirmManualCoreChecks", baselineScript);
    }

    [Fact]
    public void AppPatch_IncludesDoubleClickInstallerAndIntegrityChecks()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string installerScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Apply-AppPatch.ps1"),
            Encoding.UTF8);
        string installerCmd = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Install-AppPatch.cmd"),
            Encoding.UTF8);

        Assert.Contains("双击安装增量更新.cmd", publishScript);
        Assert.Contains("apply_app_patch.ps1", publishScript);
        Assert.Contains("增量更新说明.txt", publishScript);
        Assert.Contains("AppRootDirectory", installerScript);
        Assert.Contains("Get-FileSha256", installerScript);
        Assert.Contains("System.Security.Cryptography.SHA256", installerScript);
        Assert.Contains("[System.IO.File]::Replace", installerScript);
        Assert.Contains("powershell.exe", installerCmd);
        Assert.DoesNotContain("taskkill", installerCmd, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
