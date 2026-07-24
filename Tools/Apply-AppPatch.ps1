[CmdletBinding()]
param(
    [string]$PatchRoot = $PSScriptRoot,
    [string]$ConfigPath = "",
    [string]$AppRootPath = "",
    [switch]$SkipProcessCheck,
    [switch]$SkipVersionCheck
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Get-DefaultConfigPath {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = $env:LOCALAPPDATA
    }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw "无法定位 Windows 用户数据目录"
    }
    return Join-Path $localAppData "ExpressPackingMonitoring\config.json"
}

function Get-NormalizedDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "config.json 中的 AppRootDirectory 无效"
    }

    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]"\/")
}

function Resolve-AppRootCandidate {
    param([string]$CandidatePath)

    $cleanedPath = ([string]$CandidatePath).Trim()
    if ($cleanedPath.Length -ge 2) {
        $firstCharacter = $cleanedPath[0]
        $lastCharacter = $cleanedPath[$cleanedPath.Length - 1]
        if (($firstCharacter -eq '"' -and $lastCharacter -eq '"') -or
            ($firstCharacter -eq "'" -and $lastCharacter -eq "'")) {
            $cleanedPath = $cleanedPath.Substring(1, $cleanedPath.Length - 2).Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($cleanedPath)) {
        throw "没有提供安装位置"
    }

    $resolvedInput = [System.IO.Path]::GetFullPath($cleanedPath)
    $candidateDirectories = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $resolvedInput -PathType Leaf) {
        $candidateDirectories.Add((Split-Path -Parent $resolvedInput))
    }
    elseif (Test-Path -LiteralPath $resolvedInput -PathType Container) {
        $candidateDirectories.Add($resolvedInput)
    }
    else {
        throw "路径不存在：$cleanedPath"
    }

    $inputDirectory = $candidateDirectories[0]
    $candidateDirectories.Add((Join-Path $inputDirectory "app"))
    foreach ($directory in $candidateDirectories) {
        $normalizedDirectory = Get-NormalizedDirectory -Path $directory
        $exePath = Join-Path $normalizedDirectory "ExpressPackingMonitoring.exe"
        $dllPath = Join-Path $normalizedDirectory "ExpressPackingMonitoring.dll"
        if ((Test-Path -LiteralPath $exePath -PathType Leaf) -and
            (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
            return $normalizedDirectory
        }
    }

    throw "无法识别安装目录，请拖入完整包根目录、app 目录或其中的 ExpressPackingMonitoring.exe"
}

function Request-AppRootDirectory {
    while ($true) {
        Write-Host ""
        Write-Host "未能从 config.json 自动找到软件安装目录。" -ForegroundColor Yellow
        Write-Host "请把安装文件夹或 ExpressPackingMonitoring.exe 拖到此窗口，然后按 Enter。"
        $draggedPath = Read-Host "安装位置"
        try {
            return Resolve-AppRootCandidate -CandidatePath $draggedPath
        }
        catch {
            Write-Host $_.Exception.Message -ForegroundColor Yellow
        }
    }
}

function Test-PathWithinDirectory {
    param(
        [string]$Path,
        [string]$Directory
    )

    $directoryPrefix = $Directory.TrimEnd([char[]]"\/") + [System.IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($directoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-VersionNumber {
    param([string]$Value)

    if ($Value -match "(\d+\.\d+\.\d+(?:\.\d+)?)") {
        return [Version]$Matches[1]
    }
    throw "无法识别版本号：$Value"
}

function Get-InstalledVersion {
    param([string]$DllPath)

    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath)
    foreach ($candidate in @($info.ProductVersion, $info.FileVersion)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            $candidate -match "(\d+\.\d+\.\d+(?:\.\d+)?)") {
            return [Version]$Matches[1]
        }
    }
    throw "无法读取当前程序版本：$DllPath"
}

function Get-FileSha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-TargetProcesses {
    param([string]$TargetExePath)

    $targetFullPath = [System.IO.Path]::GetFullPath($TargetExePath)
    return @(Get-Process -Name "ExpressPackingMonitoring" -ErrorAction SilentlyContinue | Where-Object {
        try {
            -not [string]::IsNullOrWhiteSpace($_.Path) -and
                [string]::Equals(
                    [System.IO.Path]::GetFullPath($_.Path),
                    $targetFullPath,
                    [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
}

function Stop-TargetApplication {
    param([string]$TargetExePath)

    foreach ($process in @(Get-TargetProcesses -TargetExePath $TargetExePath)) {
        Write-Host "检测到程序正在运行，正在请求安全退出..." -ForegroundColor Yellow
        if (-not $process.CloseMainWindow()) {
            throw "无法请求程序退出，请手动关闭程序后重新双击更新脚本"
        }
        if (-not $process.WaitForExit(60000)) {
            throw "等待程序退出超时，请确认录像已经保存并手动关闭程序"
        }
    }
}

function Write-UpdateLog {
    param(
        [string]$LogPath,
        [string]$Message
    )

    try {
        $logDirectory = Split-Path -Parent $LogPath
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        $line = "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}" -f [DateTime]::Now, $Message
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    }
    catch {
    }
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}

$ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
$PatchRoot = [System.IO.Path]::GetFullPath($PatchRoot)
$userDataDirectory = Split-Path -Parent $ConfigPath
$logPath = Join-Path $userDataDirectory "log\manual_update.log"
$mutex = [System.Threading.Mutex]::new($false, "Local\ExpressPackingMonitoring.ManualPatch")
$ownsMutex = $false
$exitCode = 0
$appliedFiles = New-Object System.Collections.Generic.List[object]
$backupRoot = ""

try {
    try {
        $ownsMutex = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        throw "另一个增量更新正在执行"
    }

    Write-Host "正在检查增量更新包..." -ForegroundColor Cyan
    $configuredAppRoot = ""
    try {
        if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
            throw "config.json does not exist"
        }
        $config = Get-Content -Raw -Encoding UTF8 -LiteralPath $ConfigPath | ConvertFrom-Json
        $appRootProperty = if ($null -eq $config) { $null } else { $config.PSObject.Properties["AppRootDirectory"] }
        if ($null -eq $appRootProperty -or [string]::IsNullOrWhiteSpace([string]$appRootProperty.Value)) {
            throw "config.json does not contain AppRootDirectory"
        }
        $configuredAppRoot = Resolve-AppRootCandidate -CandidatePath ([string]$appRootProperty.Value)
    }
    catch {
        Write-UpdateLog -LogPath $logPath -Message "Unable to read config for manual patch: $($_.Exception.Message)"
    }

    if (-not [string]::IsNullOrWhiteSpace($configuredAppRoot)) {
        $appRoot = $configuredAppRoot
    }
    elseif (-not [string]::IsNullOrWhiteSpace($AppRootPath)) {
        $appRoot = Resolve-AppRootCandidate -CandidatePath $AppRootPath
    }
    else {
        $appRoot = Request-AppRootDirectory
    }

    $targetExePath = Join-Path $appRoot "ExpressPackingMonitoring.exe"
    $targetDllPath = Join-Path $appRoot "ExpressPackingMonitoring.dll"

    $manifestPath = Join-Path $PatchRoot "patch_manifest.json"
    $patchFilesRoot = Join-Path $PatchRoot "files"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "增量更新包缺少 patch_manifest.json"
    }
    if (-not (Test-Path -LiteralPath $patchFilesRoot -PathType Container)) {
        throw "增量更新包缺少 files 目录"
    }

    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    if (-not [string]::Equals([string]$manifest.type, "baseline_patch", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "不支持的增量更新包类型"
    }

    $baselineVersion = Get-VersionNumber -Value ([string]$manifest.patch_baseline_version)
    $latestVersion = Get-VersionNumber -Value ([string]$manifest.latest_version)
    if (-not $SkipVersionCheck) {
        $installedVersion = Get-InstalledVersion -DllPath $targetDllPath
        if ($installedVersion -ge $latestVersion) {
            Write-Host "当前已是 v$installedVersion，无需重复更新" -ForegroundColor Green
            Write-UpdateLog -LogPath $logPath -Message "Manual patch skipped: installed=$installedVersion, latest=$latestVersion"
            return
        }
        if ($installedVersion -lt $baselineVersion) {
            throw "当前版本 v$installedVersion 低于增量包基线 v$baselineVersion，请下载完整包更新"
        }
    }

    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -eq 0) {
        throw "补丁清单没有可安装文件"
    }

    $validatedFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in $manifestFiles) {
        $relativePath = ([string]$file.path).Trim().Replace("/", "\")
        $segments = @($relativePath -split "[\\/]")
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $segments -contains "." -or
            $segments -contains "..") {
            throw "补丁清单包含不安全路径：$relativePath"
        }

        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $patchFilesRoot $relativePath))
        $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $appRoot $relativePath))
        if (-not (Test-PathWithinDirectory -Path $sourcePath -Directory $patchFilesRoot) -or
            -not (Test-PathWithinDirectory -Path $destinationPath -Directory $appRoot)) {
            throw "补丁文件超出允许目录：$relativePath"
        }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "补丁文件不存在：$relativePath"
        }

        $expectedSize = [long]$file.size
        $actualSize = (Get-Item -LiteralPath $sourcePath).Length
        if ($actualSize -ne $expectedSize) {
            throw "补丁文件大小校验失败：$relativePath"
        }

        $expectedHash = ([string]$file.sha256).Trim().ToLowerInvariant()
        $actualHash = Get-FileSha256 -Path $sourcePath
        if ([string]::IsNullOrWhiteSpace($expectedHash) -or $actualHash -ne $expectedHash) {
            throw "补丁文件 SHA256 校验失败：$relativePath"
        }

        $validatedFiles.Add([pscustomobject]@{
            RelativePath = $relativePath
            SourcePath = $sourcePath
            DestinationPath = $destinationPath
            ExpectedHash = $expectedHash
        })
    }

    if (-not $SkipProcessCheck) {
        Stop-TargetApplication -TargetExePath $targetExePath
    }
    if (@(Get-TargetProcesses -TargetExePath $targetExePath).Count -gt 0) {
        throw "程序仍在运行，请关闭后重试"
    }

    $backupRoot = Join-Path $userDataDirectory (
        "cache\manual_updates\backup_{0}_{1:yyyyMMdd_HHmmss}" -f $latestVersion, [DateTime]::Now)
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    Write-UpdateLog -LogPath $logPath -Message "Manual patch start: latest=$latestVersion, appRoot=$appRoot"

    foreach ($file in $validatedFiles) {
        $destinationDirectory = Split-Path -Parent $file.DestinationPath
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        $destinationExisted = Test-Path -LiteralPath $file.DestinationPath -PathType Leaf
        $backupPath = Join-Path $backupRoot $file.RelativePath
        if ($destinationExisted) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
            Copy-Item -LiteralPath $file.DestinationPath -Destination $backupPath -Force
        }

        $appliedFiles.Add([pscustomobject]@{
            DestinationPath = $file.DestinationPath
            BackupPath = $backupPath
            DestinationExisted = $destinationExisted
        })

        $tempPath = $file.DestinationPath + ".manual-update-" + [Guid]::NewGuid().ToString("N")
        $replaceBackupPath = $file.DestinationPath + ".manual-replace-backup-" + [Guid]::NewGuid().ToString("N")
        try {
            Copy-Item -LiteralPath $file.SourcePath -Destination $tempPath -Force
            if ($destinationExisted) {
                [System.IO.File]::Replace($tempPath, $file.DestinationPath, $replaceBackupPath, $true)
            }
            else {
                [System.IO.File]::Move($tempPath, $file.DestinationPath)
            }
        }
        finally {
            try {
                if (Test-Path -LiteralPath $tempPath) {
                    Remove-Item -LiteralPath $tempPath -Force
                }
                if (Test-Path -LiteralPath $replaceBackupPath) {
                    Remove-Item -LiteralPath $replaceBackupPath -Force
                }
            }
            catch {
            }
        }
    }

    foreach ($file in $validatedFiles) {
        $installedHash = Get-FileSha256 -Path $file.DestinationPath
        if ($installedHash -ne $file.ExpectedHash) {
            throw "更新后文件校验失败：$($file.RelativePath)"
        }
    }

    if (-not $SkipVersionCheck) {
        $updatedVersion = Get-InstalledVersion -DllPath $targetDllPath
        if ($updatedVersion -ne $latestVersion) {
            throw "更新后版本校验失败：v$updatedVersion != v$latestVersion"
        }
    }

    try {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }
    catch {
        Write-UpdateLog -LogPath $logPath -Message "Backup cleanup deferred: $backupRoot, $($_.Exception.Message)"
    }
    $backupRoot = ""
    Write-UpdateLog -LogPath $logPath -Message "Manual patch completed: latest=$latestVersion"
    Write-Host ""
    Write-Host "增量更新完成，已升级到 v$latestVersion" -ForegroundColor Green
    Write-Host "请从原来的根目录启动器重新打开软件"
}
catch {
    $failure = $_.Exception.Message
    if ($appliedFiles.Count -gt 0) {
        Write-Host "更新失败，正在恢复原文件..." -ForegroundColor Yellow
        for ($index = $appliedFiles.Count - 1; $index -ge 0; $index--) {
            $applied = $appliedFiles[$index]
            try {
                if ($applied.DestinationExisted -and (Test-Path -LiteralPath $applied.BackupPath -PathType Leaf)) {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $applied.DestinationPath) | Out-Null
                    Copy-Item -LiteralPath $applied.BackupPath -Destination $applied.DestinationPath -Force
                }
                elseif (-not $applied.DestinationExisted -and (Test-Path -LiteralPath $applied.DestinationPath)) {
                    Remove-Item -LiteralPath $applied.DestinationPath -Force
                }
            }
            catch {
                Write-UpdateLog -LogPath $logPath -Message "Rollback failed: $($applied.DestinationPath), $($_.Exception.Message)"
            }
        }
    }

    Write-UpdateLog -LogPath $logPath -Message "Manual patch failed: $failure"
    Write-Host ""
    Write-Host "增量更新失败：$failure" -ForegroundColor Red
    Write-Host "原程序文件已尽可能恢复，详情见：$logPath"
    $exitCode = 1
}
finally {
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}

exit $exitCode
