param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$Version = "",
    [string]$BaselineAppDir = "",
    [string]$BaselineLauncherPath = "",
    [string]$BaselineLauncherManifestPath = "",
    [string]$SevenZipPath = "",
    [string]$PatchBaselineVersion = "0.0.18",
    [switch]$SkipTtsCacheGeneration,
    [switch]$ConfirmManualCoreChecks,
    [switch]$DisablePatch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$launcherProject = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"
$releaseValidationScript = Join-Path $repoRoot "Tools\Test-Release.ps1"
$installerBuildScript = Join-Path $repoRoot "Tools\Build-Installer.ps1"
$ttsCacheBuilderProject = Join-Path $repoRoot "Tools\ExpressPackingMonitoring.TtsCacheBuilder\ExpressPackingMonitoring.TtsCacheBuilder.csproj"
$manualPatchCmdSource = Join-Path $repoRoot "Tools\Install-AppPatch.cmd"
$manualPatchScriptSource = Join-Path $repoRoot "Tools\Apply-AppPatch.ps1"
$manualInstallerCmdName = "双击安装增量更新.cmd"
$manualInstallerScriptName = "apply_app_patch.ps1"
$manualInstallerNoticeName = "增量更新说明.txt"

function Invoke-CoreRegressionTests {
    if (-not (Test-Path $releaseValidationScript)) {
        throw "Release validation script not found: $releaseValidationScript"
    }

    & $releaseValidationScript -Configuration $Configuration
}

function Invoke-DotNetPublish {
    param([string[]]$Arguments)

    & dotnet publish @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

function New-DefaultTtsCache {
    $targetDir = Join-Path $repoRoot "package\tts_cache"
    if ($SkipTtsCacheGeneration) {
        Write-Host "Default TTS cache generation skipped by option."
        return
    }

    if (-not (Test-Path $ttsCacheBuilderProject)) {
        throw "TTS cache builder project not found: $ttsCacheBuilderProject"
    }

    $tempDir = Join-Path $repoRoot "package\.tts_cache_generation"
    if (Test-Path $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    if (Test-Path $targetDir) {
        Copy-Item -Path (Join-Path $targetDir "*") -Destination $tempDir -Recurse -Force
    }

    try {
        Write-Host "Generating default TTS cache..."
        dotnet run --project $ttsCacheBuilderProject -c $Configuration -- $tempDir
        if ($LASTEXITCODE -ne 0) {
            throw "Default TTS cache generation failed with exit code $LASTEXITCODE"
        }

        $cacheFiles = @(Get-ChildItem -LiteralPath $tempDir -File |
            Where-Object { $_.Extension -in ".mp3", ".wav" })
        if ($cacheFiles.Count -eq 0) {
            throw "Default TTS cache generation produced no audio files."
        }

        if (Test-Path $targetDir) {
            Remove-Item -LiteralPath $targetDir -Recurse -Force
        }
        Move-Item -LiteralPath $tempDir -Destination $targetDir
        Write-Host "Default TTS cache generated: $($cacheFiles.Count) files"
    }
    finally {
        if (Test-Path $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force
        }
    }
}

function Get-PackageVersion {
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        return $Version.Trim()
    }

    $tagsAtHead = @(& git -C $repoRoot tag --points-at HEAD)
    if ($LASTEXITCODE -eq 0) {
        $tag = $tagsAtHead | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($tag)) {
            return $tag.Trim()
        }
    }

    $description = (& git -C $repoRoot describe --tags --always --dirty 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($description)) {
        return $description.Trim()
    }

    return "0.0.0-local"
}

function Get-GitCommitId {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($commit)) {
        return $commit.Trim()
    }

    return ""
}

function ConvertTo-SafePathName {
    param([string]$Name)

    $safeName = $Name
    foreach ($char in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safeName = $safeName.Replace($char, "_")
    }

    return $safeName
}

$packageVersion = Get-PackageVersion
$packageName = ConvertTo-SafePathName "ExpressPackingMonitoring+$packageVersion"
$defaultPackageVersionRoot = Join-Path $repoRoot "package\$packageName"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $defaultPackageVersionRoot $packageName
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$zipFullPath = if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    [System.IO.Path]::GetFullPath((Join-Path $defaultPackageVersionRoot "$packageName.zip"))
} else {
    [System.IO.Path]::GetFullPath($ZipPath)
}
$sevenZipFullPath = [System.IO.Path]::ChangeExtension($zipFullPath, ".7z")
$packageArtifactRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $zipFullPath))
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
if (-not $outputFullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must be inside the repository: $outputFullPath"
}
if (-not $zipFullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ZipPath must be inside the repository: $zipFullPath"
}
if ([string]::Equals($packageArtifactRoot, $outputFullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
    $packageArtifactRoot.StartsWith(($outputFullPath.TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ZipPath must not be inside OutputDir, otherwise the package may include itself: $zipFullPath"
}

Invoke-CoreRegressionTests
if (-not $ConfirmManualCoreChecks) {
    Write-Warning "Manual core business and recovery checks are not confirmed. Packaging will continue; review RELEASE_CHECKLIST.md and report any unverified real-device scenarios with the release."
}

if (Test-Path $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
if (Test-Path $zipFullPath) {
    Remove-Item -LiteralPath $zipFullPath -Force
}
if (Test-Path $sevenZipFullPath) {
    Remove-Item -LiteralPath $sevenZipFullPath -Force
}

function Remove-PackageRuntimeState {
    param([string]$AppDir)

    $filePatterns = @(
        "config.json",
        "videos.db",
        "videos.db-*",
        "orderinfo_cache.json",
        "*.log",
        "*.audio.log",
        "audio_probe*.wav",
        "audio_probe*.mkv",
        "audio_probe*.mp4",
        "audio_probe*_decoded.wav",
        "*.mkv",
        "*.mp4"
    )

    foreach ($pattern in $filePatterns) {
        Get-ChildItem -LiteralPath $AppDir -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    $directories = @("tts_cache", "transcache", "Videos")
    foreach ($dir in $directories) {
        Get-ChildItem -LiteralPath $AppDir -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $dir } |
            Remove-Item -Recurse -Force
    }
}

function Copy-PackageTtsCache {
    param([string]$AppDir)

    $sourceDir = Join-Path $repoRoot "package\tts_cache"
    if (-not (Test-Path $sourceDir)) {
        Write-Host "Package tts_cache not found, skipped: $sourceDir"
        return
    }

    $targetDir = Join-Path $AppDir "tts_cache"
    if (Test-Path $targetDir) {
        Remove-Item -LiteralPath $targetDir -Recurse -Force
    }

    Copy-Item -LiteralPath $sourceDir -Destination $targetDir -Recurse -Force
    Write-Host "Package tts_cache copied: $targetDir"
}

function Compress-PackageWithRetry {
    param(
        [string]$SourceDir,
        [string]$DestinationZip
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $lastError = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            if (Test-Path $DestinationZip) {
                Remove-Item -LiteralPath $DestinationZip -Force
            }

            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $SourceDir,
                $DestinationZip,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false,
                [System.Text.Encoding]::UTF8)
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }

    throw $lastError
}

function Resolve-SevenZipExecutable {
    if (-not [string]::IsNullOrWhiteSpace($SevenZipPath)) {
        $candidate = [System.IO.Path]::GetFullPath($SevenZipPath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
        throw "SevenZipPath does not point to 7z.exe: $candidate"
    }

    $environmentPath = $env:SEVEN_ZIP_EXE
    if (-not [string]::IsNullOrWhiteSpace($environmentPath)) {
        $candidate = [System.IO.Path]::GetFullPath($environmentPath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
        throw "SEVEN_ZIP_EXE does not point to 7z.exe: $candidate"
    }

    $command = Get-Command "7z.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    throw "7-Zip was not found. Install it with: winget install --id 7zip.7zip -e -s winget"
}

function Compress-Package7zWithRetry {
    param(
        [string]$SourceDir,
        [string]$DestinationArchive,
        [string]$SevenZipExecutable
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (Test-Path -LiteralPath $DestinationArchive) {
                Remove-Item -LiteralPath $DestinationArchive -Force
            }

            Push-Location $SourceDir
            try {
                & $SevenZipExecutable a `
                    -t7z `
                    -mx=9 `
                    -m0=lzma2 `
                    -ms=on `
                    -mmt=on `
                    -bso0 `
                    -bsp0 `
                    -- `
                    $DestinationArchive `
                    ".\*"
                if ($LASTEXITCODE -ne 0) {
                    throw "7-Zip creation failed with exit code $LASTEXITCODE"
                }
            }
            finally {
                Pop-Location
            }

            & $SevenZipExecutable t -bso0 -bsp0 -- $DestinationArchive
            if ($LASTEXITCODE -ne 0) {
                throw "7-Zip integrity test failed with exit code $LASTEXITCODE"
            }
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }

    throw $lastError
}

function Test-SevenZipContainsEntry {
    param(
        [string]$ArchivePath,
        [string]$EntryName,
        [string]$SevenZipExecutable
    )

    $listing = @(& $SevenZipExecutable l -slt -ba -- $ArchivePath)
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip listing failed with exit code $LASTEXITCODE"
    }
    $expectedLine = "Path = " + $EntryName.Replace("/", "\")
    return $listing -contains $expectedLine
}

function Get-NormalizedReleaseVersion {
    param([string]$RawVersion)

    $value = $RawVersion.Trim()
    if ($value.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    $suffixIndex = $value.IndexOfAny(@('+', '-'))
    if ($suffixIndex -ge 0) {
        $value = $value.Substring(0, $suffixIndex)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return "0.0.0"
    }

    return $value
}

function Test-ZipContainsEntry {
    param(
        [string]$ZipFile,
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::Equals($entry.FullName.Replace('\', '/'), $EntryName, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }
    finally {
        $zip.Dispose()
    }
}

function ConvertFrom-Utf8Base64 {
    param([string]$Value)

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Value))
}

function Get-DotEnvValue {
    param([string]$Key)

    $paths = @(
        (Join-Path $repoRoot ".env"),
        (Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\.env"),
        (Join-Path $repoRoot "ExpressPackingMonitoring\.env")
    )

    foreach ($path in $paths) {
        if (-not (Test-Path $path)) {
            continue
        }

        foreach ($line in [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)) {
            $trimmed = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
                continue
            }

            $separatorIndex = $trimmed.IndexOf("=")
            if ($separatorIndex -le 0) {
                continue
            }

            $name = $trimmed.Substring(0, $separatorIndex).Trim()
            if (-not [string]::Equals($name, $Key, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $value = $trimmed.Substring($separatorIndex + 1).Trim()
            if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                ($value.StartsWith("'") -and $value.EndsWith("'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }

            return $value
        }
    }

    return ""
}

function Get-ConfiguredValue {
    param(
        [string]$Key,
        [string]$DefaultValue
    )

    $envValue = [Environment]::GetEnvironmentVariable($Key)
    if (-not [string]::IsNullOrWhiteSpace($envValue)) {
        return $envValue.Trim()
    }

    $dotEnvValue = Get-DotEnvValue -Key $Key
    if (-not [string]::IsNullOrWhiteSpace($dotEnvValue)) {
        return $dotEnvValue.Trim()
    }

    return $DefaultValue
}

function Get-ReleaseUrlBase {
    $explicitBase = Get-ConfiguredValue -Key "RELEASE_URL_BASE" -DefaultValue ""
    if (-not [string]::IsNullOrWhiteSpace($explicitBase)) {
        return $explicitBase.TrimEnd("/")
    }

    $checkUrl = Get-ConfiguredValue -Key "UPDATE_CHECK_URL" -DefaultValue "https://api.github.com/repos/m-RNA/ExpressPackingMonitoring/releases/latest"
    if ($checkUrl -match "^https://api\.github\.com/repos/([^/]+/[^/]+)/releases/latest/?$") {
        return "https://github.com/$($Matches[1])/releases"
    }

    if ($checkUrl -match "^https://gitee\.com/api/v5/repos/([^/]+/[^/]+)/releases/latest/?$") {
        return "https://gitee.com/$($Matches[1])/releases"
    }

    return "https://github.com/m-RNA/ExpressPackingMonitoring/releases"
}

function Expand-ReleaseTemplate {
    param(
        [string]$Template,
        [string]$ReleaseTag,
        [string]$FileName
    )

    return $Template.Replace("{tag}", $ReleaseTag).Replace("{file}", $FileName)
}

function Get-RelativePath {
    param(
        [string]$BaseDir,
        [string]$Path
    )

    $baseUri = [System.Uri](([System.IO.Path]::GetFullPath($BaseDir).TrimEnd('\') + '\'))
    $pathUri = [System.Uri]([System.IO.Path]::GetFullPath($Path))
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Copy-FilePreservingRelativePath {
    param(
        [string]$SourceFile,
        [string]$DestinationRoot,
        [string]$RelativePath
    )

    $target = Join-Path $DestinationRoot $RelativePath
    $targetParent = Split-Path -Parent $target
    if (-not [string]::IsNullOrWhiteSpace($targetParent)) {
        New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    }

    Copy-Item -LiteralPath $SourceFile -Destination $target -Force
}

function Get-LauncherSourceFingerprint {
    param([string[]]$RelativePaths)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($relativePath in $RelativePaths) {
            $normalizedPath = $relativePath.Replace('\', '/')
            $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($normalizedPath)
            $sha.TransformBlock($pathBytes, 0, $pathBytes.Length, $null, 0) | Out-Null
            $separator = [byte[]](0)
            $sha.TransformBlock($separator, 0, $separator.Length, $null, 0) | Out-Null

            $fullPath = Join-Path $repoRoot $relativePath
            if (-not (Test-Path $fullPath)) {
                throw "Launcher fingerprint file missing: $relativePath"
            }

            $content = [System.IO.File]::ReadAllBytes($fullPath)
            $sha.TransformBlock($content, 0, $content.Length, $null, 0) | Out-Null
            $sha.TransformBlock($separator, 0, $separator.Length, $null, 0) | Out-Null
        }

        $emptyBytes = New-Object byte[] 0
        $sha.TransformFinalBlock($emptyBytes, 0, 0) | Out-Null
        return [System.BitConverter]::ToString($sha.Hash).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Read-LauncherFingerprintFromManifest {
    param([string]$ManifestPath)

    try {
        $manifest = Get-Content -Raw -Encoding UTF8 $ManifestPath | ConvertFrom-Json
        return [string]$manifest.launcher_source_fingerprint
    }
    catch {
        return ""
    }
}

function New-AppPatchPackage {
    param(
        [string]$CurrentAppDir,
        [string]$BaselineDir,
        [string]$PatchZipPath,
        [string]$BaselineVersion,
        [string]$LatestVersion,
        [string]$ManualInstallerCmdPath,
        [string]$ManualInstallerScriptPath
    )

    if (-not (Test-Path $BaselineDir)) {
        throw "BaselineAppDir does not exist: $BaselineDir"
    }

    $patchWorkDir = Join-Path ([System.IO.Path]::GetDirectoryName($PatchZipPath)) ("_patch_work_" + [System.IO.Path]::GetFileNameWithoutExtension($PatchZipPath))
    $patchFilesDir = Join-Path $patchWorkDir "files"
    if (Test-Path $patchWorkDir) {
        Remove-Item -LiteralPath $patchWorkDir -Recurse -Force
    }
    if (Test-Path $PatchZipPath) {
        Remove-Item -LiteralPath $PatchZipPath -Force
    }
    New-Item -ItemType Directory -Force -Path $patchFilesDir | Out-Null

    $changedFiles = @()
    Get-ChildItem -LiteralPath $CurrentAppDir -File -Recurse | ForEach-Object {
        $relativePath = Get-RelativePath -BaseDir $CurrentAppDir -Path $_.FullName
        if ($relativePath.StartsWith("tts_cache\", [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $baselineFile = Join-Path $BaselineDir $relativePath
        $currentHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $isChanged = $true

        if (Test-Path $baselineFile) {
            $baselineHash = (Get-FileHash -LiteralPath $baselineFile -Algorithm SHA256).Hash.ToLowerInvariant()
            $isChanged = -not [string]::Equals($currentHash, $baselineHash, [System.StringComparison]::OrdinalIgnoreCase)
        }

        if ($isChanged) {
            Copy-FilePreservingRelativePath -SourceFile $_.FullName -DestinationRoot $patchFilesDir -RelativePath $relativePath
            $changedFiles += [ordered]@{
                "path" = $relativePath.Replace('\', '/')
                "sha256" = $currentHash
                "size" = $_.Length
            }
        }
    }

    if ($changedFiles.Count -eq 0) {
        Remove-Item -LiteralPath $patchWorkDir -Recurse -Force
        throw "AppPatch package has no changed files. Check BaselineAppDir or disable patch for this release."
    }

    $patchManifest = [ordered]@{}
    $patchManifest["type"] = "baseline_patch"
    $patchManifest["patch_baseline_version"] = $BaselineVersion
    $patchManifest["latest_version"] = $LatestVersion
    $patchManifest["files"] = $changedFiles
    $patchManifest |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $patchWorkDir "patch_manifest.json") -Encoding UTF8

    if (-not (Test-Path -LiteralPath $ManualInstallerCmdPath -PathType Leaf)) {
        throw "Manual AppPatch CMD installer not found: $ManualInstallerCmdPath"
    }
    if (-not (Test-Path -LiteralPath $ManualInstallerScriptPath -PathType Leaf)) {
        throw "Manual AppPatch PowerShell installer not found: $ManualInstallerScriptPath"
    }

    Copy-Item -LiteralPath $ManualInstallerCmdPath -Destination (Join-Path $patchWorkDir $manualInstallerCmdName) -Force
    Copy-Item -LiteralPath $ManualInstallerScriptPath -Destination (Join-Path $patchWorkDir $manualInstallerScriptName) -Force

    $manualInstallerNotice = @(
        "快递打包监控增量更新说明"
        ""
        "1. 请先完整解压增量更新包，不要直接在压缩软件中运行脚本。"
        "2. 双击《$manualInstallerCmdName》。"
        "3. 脚本会从 config.json 读取原 app 目录，校验补丁并请求正在运行的软件正常退出。"
        "4. 更新成功后，请从原来的根目录 ExpressPackingMonitoring.exe 启动软件。"
        ""
        "请勿单独移动 CMD、apply_app_patch.ps1、patch_manifest.json 或 files 文件夹。"
        "如果脚本无法自动定位软件，请把完整包根目录、app 目录或 ExpressPackingMonitoring.exe 拖到窗口中。"
        "脚本会自动识别实际 app 目录并判断当前版本是否适用此增量包。"
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $patchWorkDir $manualInstallerNoticeName) -Value $manualInstallerNotice -Encoding UTF8

    Compress-PackageWithRetry -SourceDir $patchWorkDir -DestinationZip $PatchZipPath
    Remove-Item -LiteralPath $patchWorkDir -Recurse -Force
}

$appPublishDir = Join-Path $outputFullPath "app"
$appBaseOutput = Join-Path $repoRoot "ExpressPackingMonitoring\bin_publish_tmp\clean-package-app\"
$appBaseIntermediate = Join-Path $repoRoot "ExpressPackingMonitoring\obj_publish_tmp\clean-package-app\"
$launcherBaseOutput = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\bin_publish_tmp\clean-package-launcher\"
$launcherBaseIntermediate = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\obj_publish_tmp\clean-package-launcher\"
$gitCommitId = Get-GitCommitId
$packageUpdateCheckUrl = Get-ConfiguredValue -Key "UPDATE_CHECK_URL" -DefaultValue "https://api.github.com/repos/m-RNA/ExpressPackingMonitoring/releases/latest"

New-DefaultTtsCache

Invoke-DotNetPublish -Arguments @(
    $appProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:InformationalVersion=$packageVersion",
    "-p:GitCommitId=$gitCommitId",
    "-p:PublishSingleFile=false",
    "-p:BaseOutputPath=$appBaseOutput",
    "-p:BaseIntermediateOutputPath=$appBaseIntermediate",
    "-p:PublishDir=$appPublishDir\"
)

Invoke-DotNetPublish -Arguments @(
    $launcherProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:BaseOutputPath=$launcherBaseOutput",
    "-p:BaseIntermediateOutputPath=$launcherBaseIntermediate",
    "-p:LauncherDefaultUpdateCheckUrl=$packageUpdateCheckUrl",
    "-p:PublishDir=$outputFullPath\"
)

$launcherExe = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
if (-not (Test-Path $launcherExe)) {
    $nativeLauncher = Get-ChildItem -LiteralPath $launcherBaseOutput -Recurse -Filter "ExpressPackingMonitoring.exe" |
        Where-Object { $_.FullName -like "*\native\*" } |
        Select-Object -First 1
    if ($null -eq $nativeLauncher) {
        throw "Launcher publish did not produce ExpressPackingMonitoring.exe"
    }
    Copy-Item -LiteralPath $nativeLauncher.FullName -Destination $launcherExe -Force
}

Get-ChildItem -LiteralPath $outputFullPath -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".pdb", ".dbg" } |
    Remove-Item -Force

Remove-PackageRuntimeState -AppDir $appPublishDir
Copy-PackageTtsCache -AppDir $appPublishDir
$publishedTtsCacheFiles = @(Get-ChildItem -LiteralPath (Join-Path $appPublishDir "tts_cache") -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".mp3", ".wav" })
if (-not $SkipTtsCacheGeneration -and $publishedTtsCacheFiles.Count -eq 0) {
    throw "Clean package validation failed: default TTS cache is empty"
}

$launcherExe = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
$appExe = Join-Path $appPublishDir "ExpressPackingMonitoring.exe"
$requiredAppRuntimeFiles = @(
    "zxing.dll",
    "OpenCvSharp.dll",
    "OpenCvSharp.WpfExtensions.dll",
    "OpenCvSharpExtern.dll"
)
if (-not (Test-Path $launcherExe)) {
    throw "Clean package validation failed: missing root launcher"
}
if (-not (Test-Path $appExe)) {
    throw "Clean package validation failed: missing app\ExpressPackingMonitoring.exe"
}
foreach ($runtimeFile in $requiredAppRuntimeFiles) {
    if (-not (Test-Path (Join-Path $appPublishDir $runtimeFile))) {
        throw "Clean package validation failed: missing camera barcode runtime dependency app\$runtimeFile"
    }
}

$normalizedVersion = Get-NormalizedReleaseVersion $packageVersion
$normalizedPatchBaselineVersion = Get-NormalizedReleaseVersion $PatchBaselineVersion
$releaseTag = "v$normalizedVersion"
$packageRoot = $packageArtifactRoot
$legacyAppFullZipPath = Join-Path $packageRoot "ExpressPackingMonitoring_AppFull_$releaseTag.zip"
$appPatchZipName = "ExpressPackingMonitoring_AppPatch_$releaseTag.zip"
$appPatchZipPath = Join-Path $packageRoot $appPatchZipName
$updateJsonName = "update_$releaseTag.json"
$updateJsonPath = Join-Path $packageRoot $updateJsonName
$launcherManifestName = "launcher_manifest_$releaseTag.json"
$launcherManifestPath = Join-Path $packageRoot $launcherManifestName
$releaseInfoName = "release_info_$releaseTag.txt"
$releaseInfoPath = Join-Path $packageRoot $releaseInfoName
$setupFileName = "ExpressPackingMonitoring_Setup_$releaseTag.exe"
$setupPath = Join-Path $packageRoot $setupFileName
$releaseUrlBase = Get-ReleaseUrlBase
$releasePageTemplate = Get-ConfiguredValue -Key "RELEASE_PAGE_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/tag/{tag}"
$appPatchUrlTemplate = Get-ConfiguredValue -Key "APP_PATCH_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/download/{tag}/{file}"
$releasePage = Expand-ReleaseTemplate -Template $releasePageTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$appPatchPlaceholderUrl = Expand-ReleaseTemplate -Template $appPatchUrlTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$fullDownloadPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_PAGE" -DefaultValue ""
if ([string]::IsNullOrWhiteSpace($fullDownloadPageTemplate)) {
    $fullDownloadPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_PAGE_URL_TEMPLATE" -DefaultValue $releasePage
}
$fullDownloadPage = Expand-ReleaseTemplate -Template $fullDownloadPageTemplate -ReleaseTag $releaseTag -FileName (Split-Path -Leaf $zipFullPath)

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
if (Test-Path $legacyAppFullZipPath) {
    Remove-Item -LiteralPath $legacyAppFullZipPath -Force
}
if (Test-Path $appPatchZipPath) {
    Remove-Item -LiteralPath $appPatchZipPath -Force
}
if (-not (Test-Path -LiteralPath $installerBuildScript -PathType Leaf)) {
    throw "Installer build script not found: $installerBuildScript"
}
& $installerBuildScript -SourceDir $outputFullPath -Version $normalizedVersion -OutputDir $packageRoot
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer build failed: $setupPath"
}
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$setupSize = (Get-Item -LiteralPath $setupPath).Length
$setupSignatureStatus = (Get-AuthenticodeSignature -LiteralPath $setupPath).Status.ToString()

$patchSupported = $false
$patchReason = ""
$appPatchHash = ""
$appPatchSize = 0
$launcherChanged = $false
$launcherPatchBlocked = $false
$launcherCheckInfo = ""
$launcherProtocolVersion = "1"
$launcherFingerprintFiles = @(
    "ExpressPackingMonitoring.Launcher\Program.cs",
    "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"
)
$launcherSourceFingerprint = Get-LauncherSourceFingerprint -RelativePaths $launcherFingerprintFiles
$launcherManifest = [ordered]@{}
$launcherManifest["version"] = $normalizedVersion
$launcherManifest["launcher_update_protocol_version"] = $launcherProtocolVersion
$launcherManifest["launcher_source_fingerprint"] = $launcherSourceFingerprint
$launcherManifest["fingerprint_files"] = @($launcherFingerprintFiles | ForEach-Object { $_.Replace('\', '/') })
$launcherManifest |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $launcherManifestPath -Encoding UTF8

if ([string]::IsNullOrWhiteSpace($BaselineLauncherManifestPath)) {
    $launcherCheckInfo = ConvertFrom-Utf8Base64 "5pyq5qCh6aqM5ZCv5Yqo5Zmo5rqQ56CB5oyH57q577yM6K+356Gu6K6k5pys54mI5pys5pyq5L+u5pS55ZCv5Yqo5Zmo44CC"
}
elseif (-not (Test-Path $BaselineLauncherManifestPath)) {
    $launcherCheckInfo = ConvertFrom-Utf8Base64 "QmFzZWxpbmVMYXVuY2hlck1hbmlmZXN0UGF0aCDot6/lvoTkuI3lrZjlnKjvvIzlt7LnpoHnlKjlop7ph4/mm7TmlrDjgII="
    $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77yaQmFzZWxpbmVMYXVuY2hlck1hbmlmZXN0UGF0aCDot6/lvoTkuI3lrZjlnKjjgII="
    $launcherPatchBlocked = $true
}
else {
    $baselineLauncherFingerprint = Read-LauncherFingerprintFromManifest -ManifestPath $BaselineLauncherManifestPath
    $launcherChanged = [string]::IsNullOrWhiteSpace($baselineLauncherFingerprint) -or
        -not [string]::Equals($launcherSourceFingerprint, $baselineLauncherFingerprint, [System.StringComparison]::OrdinalIgnoreCase)
    if ($launcherChanged) {
        $baselineTag = "v$normalizedPatchBaselineVersion"
        & git -C $repoRoot rev-parse --verify --quiet "$baselineTag^{commit}" 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            & git -C $repoRoot diff --quiet $baselineTag -- @launcherFingerprintFiles
            if ($LASTEXITCODE -eq 0) {
                $launcherChanged = $false
                $launcherCheckInfo = "Launcher manifest byte fingerprint differs, but tracked launcher sources match $baselineTag."
            }
        }
    }
    if ($launcherChanged) {
        $launcherCheckInfo = ConvertFrom-Utf8Base64 "5ZCv5Yqo5Zmo5rqQ56CB5oyH57q55bey5Y+Y5YyW77yM5bey56aB55So5aKe6YeP5pu05paw44CC"
        $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77ya5ZCv5Yqo5Zmo5rqQ56CB5oyH57q55bey5Y+Y5YyW77yM6K+35omL5Yqo5LiL6L295a6M5pW05YyF44CC"
        $launcherPatchBlocked = $true
    }
    elseif ([string]::IsNullOrWhiteSpace($launcherCheckInfo)) {
        $launcherCheckInfo = ConvertFrom-Utf8Base64 "5ZCv5Yqo5Zmo5rqQ56CB5oyH57q55bey6YCa6L+H77yM5ZCv5Yqo5Zmo5pyq5Y+Y5YyW44CC"
    }
}
if (-not [string]::IsNullOrWhiteSpace($BaselineLauncherPath)) {
    $launcherCheckInfo += [Environment]::NewLine + (ConvertFrom-Utf8Base64 "QmFzZWxpbmVMYXVuY2hlclBhdGgg5bey5bqf5byD77yM6K+35pS555SoIEJhc2VsaW5lTGF1bmNoZXJNYW5pZmVzdFBhdGjjgII=")
}

if ($DisablePatch) {
    $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77ya5bey5Lyg5YWlIERpc2FibGVQYXRjaOOAgg=="
}
elseif ([string]::IsNullOrWhiteSpace($BaselineAppDir) -or -not (Test-Path $BaselineAppDir)) {
    $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77ya5pyq5Lyg5YWlIEJhc2VsaW5lQXBwRGlyIOaIlui3r+W+hOS4jeWtmOWcqOOAgg=="
}
elseif ($launcherPatchBlocked) {
}
else {
    New-AppPatchPackage `
        -CurrentAppDir $appPublishDir `
        -BaselineDir ([System.IO.Path]::GetFullPath($BaselineAppDir)) `
        -PatchZipPath $appPatchZipPath `
        -BaselineVersion $normalizedPatchBaselineVersion `
        -LatestVersion $normalizedVersion `
        -ManualInstallerCmdPath $manualPatchCmdSource `
        -ManualInstallerScriptPath $manualPatchScriptSource

    if (-not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "patch_manifest.json")) {
        throw "AppPatch package validation failed: missing patch_manifest.json"
    }
    foreach ($manualInstallerEntry in @($manualInstallerCmdName, $manualInstallerScriptName, $manualInstallerNoticeName)) {
        if (-not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName $manualInstallerEntry)) {
            throw "AppPatch package validation failed: missing $manualInstallerEntry"
        }
    }
    foreach ($runtimeFile in $requiredAppRuntimeFiles) {
        $baselineRuntimeFile = Join-Path ([System.IO.Path]::GetFullPath($BaselineAppDir)) $runtimeFile
        $currentRuntimeFile = Join-Path $appPublishDir $runtimeFile
        $runtimeChanged = -not (Test-Path $baselineRuntimeFile)
        if (-not $runtimeChanged) {
            $baselineRuntimeHash = (Get-FileHash -LiteralPath $baselineRuntimeFile -Algorithm SHA256).Hash
            $currentRuntimeHash = (Get-FileHash -LiteralPath $currentRuntimeFile -Algorithm SHA256).Hash
            $runtimeChanged = -not [string]::Equals($baselineRuntimeHash, $currentRuntimeHash, [System.StringComparison]::OrdinalIgnoreCase)
        }
        if ($runtimeChanged -and -not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "files/$runtimeFile")) {
            throw "AppPatch package validation failed: missing changed camera barcode runtime dependency files/$runtimeFile"
        }
    }

    $appPatchHash = (Get-FileHash -LiteralPath $appPatchZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $appPatchSize = (Get-Item -LiteralPath $appPatchZipPath).Length
    $patchSupported = $true
    $patchReason = (ConvertFrom-Utf8Base64 "5bey55Sf5oiQ5Zu65a6a5Z+657q/5aKe6YeP5YyF77ya") + $appPatchZipName
}

$updateManifest = [ordered]@{}
$updateManifest["latest_version"] = $normalizedVersion
$updateManifest["title"] = ConvertFrom-Utf8Base64 "6K+35aGr5YaZ5pu05paw5qCH6aKY"
$updateManifest["release_page"] = $releasePage
$updateManifest["patch_baseline_version"] = $normalizedPatchBaselineVersion
$updateManifest["patch_supported"] = $patchSupported
if ($patchSupported) {
    $patchPackageInfo = [ordered]@{}
    $patchPackageInfo["type"] = "baseline_patch"
    $patchPackageInfo["url"] = $appPatchPlaceholderUrl
    $patchPackageInfo["sha256"] = $appPatchHash
    $patchPackageInfo["size"] = $appPatchSize
    $updateManifest["patch_package"] = $patchPackageInfo
    $updateManifest["notes"] = @(
        (ConvertFrom-Utf8Base64 "6K+35aGr5YaZ5pu05paw5YaF5a65")
        "启动器会自动安装增量更新；手动下载时请完整解压，并双击《$manualInstallerCmdName》"
        "首次安装建议从完整下载页获取《$setupFileName》；完整 7z 是小体积免安装包，ZIP 用于系统原生解压和故障恢复"
    )
}
else {
    $updateManifest["patch_package"] = $null
    $updateManifest["notes"] = @(
        "本版本不支持自动增量更新，请下载《$setupFileName》完成升级"
        "完整 7z 是小体积免安装包，ZIP 用于系统原生解压和故障恢复"
    )
}
$updateManifest["full_download_page"] = $fullDownloadPage

$updateManifest |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $updateJsonPath -Encoding UTF8

$zipParent = Split-Path -Parent $zipFullPath
if (-not [string]::IsNullOrWhiteSpace($zipParent)) {
    New-Item -ItemType Directory -Force -Path $zipParent | Out-Null
}
Compress-PackageWithRetry -SourceDir $outputFullPath -DestinationZip $zipFullPath
$sevenZipExecutable = Resolve-SevenZipExecutable
Compress-Package7zWithRetry `
    -SourceDir $outputFullPath `
    -DestinationArchive $sevenZipFullPath `
    -SevenZipExecutable $sevenZipExecutable

if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "ExpressPackingMonitoring.exe")) {
    throw "Full zip validation failed: missing root launcher"
}
if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/ExpressPackingMonitoring.exe")) {
    throw "Full zip validation failed: missing app/ExpressPackingMonitoring.exe"
}
if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "ExpressPackingMonitoring.exe" -SevenZipExecutable $sevenZipExecutable)) {
    throw "Full 7z validation failed: missing root launcher"
}
if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "app/ExpressPackingMonitoring.exe" -SevenZipExecutable $sevenZipExecutable)) {
    throw "Full 7z validation failed: missing app/ExpressPackingMonitoring.exe"
}
foreach ($runtimeFile in $requiredAppRuntimeFiles) {
    if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/$runtimeFile")) {
        throw "Full zip validation failed: missing camera barcode runtime dependency app/$runtimeFile"
    }
    if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "app/$runtimeFile" -SevenZipExecutable $sevenZipExecutable)) {
        throw "Full 7z validation failed: missing camera barcode runtime dependency app/$runtimeFile"
    }
}
$sevenZipHash = (Get-FileHash -LiteralPath $sevenZipFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sevenZipSize = (Get-Item -LiteralPath $sevenZipFullPath).Length
$fullZipHash = (Get-FileHash -LiteralPath $zipFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$fullZipSize = (Get-Item -LiteralPath $zipFullPath).Length

$patchReleaseInfo = if ($patchSupported) { $appPatchZipName } else { $patchReason }
$releaseInfoCheckLine = (ConvertFrom-Utf8Base64 "5LiK5Lyg5ZCO6K+35qOA5p+lIA==") + $updateJsonName + (ConvertFrom-Utf8Base64 "IOmHjOeahCBwYXRjaF9wYWNrYWdlLnVybCDmmK/lkKbkuI4gUmVsZWFzZSDpmYTku7bkuIvovb3lnLDlnYDkuIDoh7TjgII=")
$patchBaselineInfoLine = (ConvertFrom-Utf8Base64 "UGF0Y2gg5Z+657q/54mI5pys77ya") + $normalizedPatchBaselineVersion
$releaseInfoLines = @()
$releaseInfoLines += ConvertFrom-Utf8Base64 "UmVsZWFzZSDkuIrkvKDmuIXljZU="
$releaseInfoLines += ""
$releaseInfoLines += (ConvertFrom-Utf8Base64 "54mI5pys77ya") + $releaseTag
$releaseInfoLines += (ConvertFrom-Utf8Base64 "UmVsZWFzZSDpobXpnaLvvJo=") + $releasePage
$releaseInfoLines += "Full download page: " + $fullDownloadPage
$releaseInfoLines += ""
$releaseInfoLines += "GitHub 默认上传："
$releaseInfoLines += "1. Windows 安装向导（推荐）：" + $setupFileName
$releaseInfoLines += "2. 完整包 7z（小体积免安装）：" + (Split-Path -Leaf $sevenZipFullPath)
$releaseInfoLines += "3. 完整包 ZIP（系统原生解压/故障恢复）：" + (Split-Path -Leaf $zipFullPath)
$releaseInfoLines += "4. " + (ConvertFrom-Utf8Base64 "QXBwUGF0Y2gg5YyF77ya") + $patchReleaseInfo
$releaseInfoLines += (ConvertFrom-Utf8Base64 "NS4g5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
$releaseInfoLines += ""
$releaseInfoLines += "Gitee 手工上传："
$releaseInfoLines += "1. " + (ConvertFrom-Utf8Base64 "QXBwUGF0Y2gg5YyF77ya") + $patchReleaseInfo
$releaseInfoLines += (ConvertFrom-Utf8Base64 "Mi4g5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
$releaseInfoLines += "Setup、完整 7z 和完整 ZIP 使用 Full download page，不上传到 Gitee"
$releaseInfoLines += "Local verification only (do not upload by default): " + $launcherManifestName
$releaseInfoLines += ""
$releaseInfoLines += "Setup SHA256:"
$releaseInfoLines += $setupHash
$releaseInfoLines += "Setup size: $setupSize bytes"
$releaseInfoLines += "Setup Authenticode status: $setupSignatureStatus"
if (-not [string]::Equals($setupSignatureStatus, "Valid", [System.StringComparison]::OrdinalIgnoreCase)) {
    $releaseInfoLines += "WARNING: Setup is unsigned; Windows SmartScreen may show an unknown publisher warning."
}
$releaseInfoLines += ""
$releaseInfoLines += "Full 7z SHA256:"
$releaseInfoLines += $sevenZipHash
$releaseInfoLines += "Full 7z size: $sevenZipSize bytes"
$releaseInfoLines += ""
$releaseInfoLines += "Full ZIP SHA256:"
$releaseInfoLines += $fullZipHash
$releaseInfoLines += "Full ZIP size: $fullZipSize bytes"
$releaseInfoLines += ""
$releaseInfoLines += $releaseInfoCheckLine
$releaseInfoLines += ""
$releaseInfoLines += $launcherCheckInfo
$releaseInfoLines += ""
$releaseInfoLines += $patchBaselineInfoLine
if ($patchSupported) {
    $releaseInfoLines += "AppPatch SHA256:"
    $releaseInfoLines += $appPatchHash
    $releaseInfoLines += ""
    $releaseInfoLines += "AppPatch size:"
    $releaseInfoLines += "$appPatchSize bytes"
    $releaseInfoLines += ""
    $releaseInfoLines += "Manual AppPatch installer:"
    $releaseInfoLines += $manualInstallerCmdName
}
$releaseInfo = $releaseInfoLines -join [Environment]::NewLine
$releaseInfo | Set-Content -LiteralPath $releaseInfoPath -Encoding UTF8

Write-Host "Clean package created: $outputFullPath"
Write-Host "Installer created: $setupPath"
Write-Host "7z package created: $sevenZipFullPath"
Write-Host "Zip package created: $zipFullPath"
if ($patchSupported) {
    Write-Host "AppPatch package created: $appPatchZipPath"
}
else {
    Write-Host "AppPatch package skipped: $patchReason"
}
Write-Host "Update manifest created: $updateJsonPath"
Write-Host "Release info created: $releaseInfoPath"
Write-Host "Root items:"
Get-ChildItem -LiteralPath $outputFullPath | Sort-Object PSIsContainer, Name | Select-Object Name, Mode, Length | Format-Table -AutoSize
