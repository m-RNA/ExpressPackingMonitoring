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
        Assert.Contains("Request-AppRootDirectory", installerScript);
        Assert.Contains("完整包根目录、app 目录", installerScript);
        Assert.Contains("WScript.Shell", installerScript);
        Assert.Contains("Shell.Application", installerScript);
        Assert.Contains("Get-FileSha256", installerScript);
        Assert.Contains("System.Security.Cryptography.SHA256", installerScript);
        Assert.Contains("[System.IO.File]::Replace", installerScript);
        Assert.Contains("powershell.exe", installerCmd);
        Assert.DoesNotContain("taskkill", installerCmd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsInstaller_UsesFixedPerUserIdentityAndSafeReleaseInputs()
    {
        string repositoryRoot = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Installer", "ExpressPackingMonitoring.iss"),
            Encoding.UTF8);
        string chineseMessages = File.ReadAllText(
            Path.Combine(repositoryRoot, "Installer", "Languages", "ChineseSimplified.isl"),
            Encoding.UTF8);
        string buildScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Build-Installer.ps1"),
            Encoding.UTF8);
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05", innoScript);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\ExpressPackingMonitoring", innoScript);
        Assert.Contains("DisableDirPage=yes", innoScript);
        Assert.Contains("PrivilegesRequired=lowest", innoScript);
        Assert.Contains("ArchitecturesAllowed=x64compatible", innoScript);
        Assert.Contains("CloseApplications=yes", innoScript);
        Assert.DoesNotContain("CloseApplications=force", innoScript);
        Assert.Contains(@"MessagesFile: ""Languages\ChineseSimplified.isl""", innoScript);
        Assert.Contains("LanguageName=简体中文", chineseMessages);
        Assert.Contains("ButtonNext=下一步", chineseMessages);
        Assert.Contains(@"Filename: ""{app}\{#MyAppExeName}""; WorkingDir: ""{app}""", innoScript);
        Assert.Contains("--uninstall-plan-recordings", innoScript);
        Assert.Contains("--uninstall-delete-recordings", innoScript);
        Assert.Contains("MB_DEFBUTTON2", innoScript);
        Assert.DoesNotContain("WizardSilent", innoScript);

        Assert.Contains("INNO_SETUP_ISCC", buildScript);
        Assert.Contains("winget install --id JRSoftware.InnoSetup", buildScript);
        Assert.Contains("WINDOWS_SIGN_CERT_THUMBPRINT", buildScript);
        Assert.Contains("Get-AuthenticodeSignature", buildScript);
        Assert.Contains("config.json", buildScript);
        Assert.Contains("videos.db", buildScript);

        Assert.Contains("ExpressPackingMonitoring_Setup_$releaseTag.exe", publishScript);
        Assert.Contains("Build-Installer.ps1", publishScript);
        Assert.Contains("SmartScreen", publishScript);
        Assert.Contains("GitHub 默认上传", publishScript);
        Assert.Contains("Gitee 手工上传", publishScript);
        Assert.Contains("Setup、完整 7z 和完整 ZIP 使用 Full download page", publishScript);
        Assert.Contains("SEVEN_ZIP_EXE", publishScript);
        Assert.Contains("winget install --id 7zip.7zip", publishScript);
        Assert.Contains("-t7z", publishScript);
        Assert.Contains("-m0=lzma2", publishScript);
        Assert.Contains("-ms=on", publishScript);
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
