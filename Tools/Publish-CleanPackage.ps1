param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$Version = "",
    [string]$BaselineAppDir = "",
    [string]$BaselineLauncherPath = "",
    [string]$BaselineLauncherManifestPath = "",
    [string]$PatchBaselineVersion = "0.0.16",
    [switch]$DisablePatch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$launcherProject = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"

function Invoke-DotNetPublish {
    dotnet publish @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
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

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "package\$packageName"
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$zipFullPath = if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    [System.IO.Path]::GetFullPath("$outputFullPath.zip")
} else {
    [System.IO.Path]::GetFullPath($ZipPath)
}
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
if (-not $outputFullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must be inside the repository: $outputFullPath"
}
if (-not $zipFullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ZipPath must be inside the repository: $zipFullPath"
}

if (Test-Path $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
if (Test-Path $zipFullPath) {
    Remove-Item -LiteralPath $zipFullPath -Force
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

function Compress-PackageWithRetry {
    param(
        [string]$SourceDir,
        [string]$DestinationZip
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            if (Test-Path $DestinationZip) {
                Remove-Item -LiteralPath $DestinationZip -Force
            }

            Get-ChildItem -LiteralPath $SourceDir -Force |
                Compress-Archive -DestinationPath $DestinationZip -CompressionLevel Optimal -Force
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }

    throw $lastError
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
        [string]$LatestVersion
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

Invoke-DotNetPublish $appProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:InformationalVersion=$packageVersion `
    -p:GitCommitId=$gitCommitId `
    -p:PublishSingleFile=false `
    -p:BaseOutputPath=$appBaseOutput `
    -p:BaseIntermediateOutputPath=$appBaseIntermediate `
    -p:PublishDir="$appPublishDir\"

Invoke-DotNetPublish $launcherProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:BaseOutputPath=$launcherBaseOutput `
    -p:BaseIntermediateOutputPath=$launcherBaseIntermediate `
    -p:LauncherDefaultUpdateCheckUrl=$packageUpdateCheckUrl `
    -p:PublishDir="$outputFullPath\"

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

$launcherExe = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
$appExe = Join-Path $appPublishDir "ExpressPackingMonitoring.exe"
if (-not (Test-Path $launcherExe)) {
    throw "Clean package validation failed: missing root launcher"
}
if (-not (Test-Path $appExe)) {
    throw "Clean package validation failed: missing app\ExpressPackingMonitoring.exe"
}

$normalizedVersion = Get-NormalizedReleaseVersion $packageVersion
$normalizedPatchBaselineVersion = Get-NormalizedReleaseVersion $PatchBaselineVersion
$releaseTag = "v$normalizedVersion"
$packageRoot = Join-Path $repoRoot "package"
$legacyAppFullZipPath = Join-Path $packageRoot "ExpressPackingMonitoring_AppFull_$releaseTag.zip"
$appPatchZipName = "ExpressPackingMonitoring_AppPatch_$releaseTag.zip"
$appPatchZipPath = Join-Path $packageRoot $appPatchZipName
$updateJsonName = "update_$releaseTag.json"
$updateJsonPath = Join-Path $packageRoot $updateJsonName
$launcherManifestName = "launcher_manifest_$releaseTag.json"
$launcherManifestPath = Join-Path $packageRoot $launcherManifestName
$releaseInfoName = "release_info_$releaseTag.txt"
$releaseInfoPath = Join-Path $packageRoot $releaseInfoName
$releaseUrlBase = Get-ReleaseUrlBase
$releasePageTemplate = Get-ConfiguredValue -Key "RELEASE_PAGE_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/tag/{tag}"
$appPatchUrlTemplate = Get-ConfiguredValue -Key "APP_PATCH_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/download/{tag}/{file}"
$releasePage = Expand-ReleaseTemplate -Template $releasePageTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$appPatchPlaceholderUrl = Expand-ReleaseTemplate -Template $appPatchUrlTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
if (Test-Path $legacyAppFullZipPath) {
    Remove-Item -LiteralPath $legacyAppFullZipPath -Force
}
if (Test-Path $appPatchZipPath) {
    Remove-Item -LiteralPath $appPatchZipPath -Force
}

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
        $launcherCheckInfo = ConvertFrom-Utf8Base64 "5ZCv5Yqo5Zmo5rqQ56CB5oyH57q55bey5Y+Y5YyW77yM5bey56aB55So5aKe6YeP5pu05paw44CC"
        $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77ya5ZCv5Yqo5Zmo5rqQ56CB5oyH57q55bey5Y+Y5YyW77yM6K+35omL5Yqo5LiL6L295a6M5pW05YyF44CC"
        $launcherPatchBlocked = $true
    }
    else {
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
        -LatestVersion $normalizedVersion

    if (-not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "patch_manifest.json")) {
        throw "AppPatch package validation failed: missing patch_manifest.json"
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
    )
}
else {
    $updateManifest["patch_package"] = $null
    $updateManifest["notes"] = if ($launcherChanged) {
        @((ConvertFrom-Utf8Base64 "5pys54mI5pys5YyF5ZCr5ZCv5Yqo5Zmo5pu05paw77yM6K+35omL5Yqo5LiL6L295a6M5pW05YyF44CC"))
    }
    else {
        @((ConvertFrom-Utf8Base64 "5pys54mI5pys5LiN5pSv5oyB6Ieq5Yqo5aKe6YeP5pu05paw77yM6K+35omL5Yqo5LiL6L295a6M5pW05YyF44CC"))
    }
}
$updateManifest["full_download_page"] = $releasePage

$updateManifest |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $updateJsonPath -Encoding UTF8

$patchReleaseInfo = if ($patchSupported) { $appPatchZipName } else { $patchReason }
$releaseInfoCheckLine = (ConvertFrom-Utf8Base64 "5LiK5Lyg5ZCO6K+35qOA5p+lIA==") + $updateJsonName + (ConvertFrom-Utf8Base64 "IOmHjOeahCBwYXRjaF9wYWNrYWdlLnVybCDmmK/lkKbkuI4gUmVsZWFzZSDpmYTku7bkuIvovb3lnLDlnYDkuIDoh7TjgII=")
$patchBaselineInfoLine = (ConvertFrom-Utf8Base64 "UGF0Y2gg5Z+657q/54mI5pys77ya") + $normalizedPatchBaselineVersion
$releaseInfoLines = @()
$releaseInfoLines += ConvertFrom-Utf8Base64 "UmVsZWFzZSDkuIrkvKDmuIXljZU="
$releaseInfoLines += ""
$releaseInfoLines += (ConvertFrom-Utf8Base64 "54mI5pys77ya") + $releaseTag
$releaseInfoLines += (ConvertFrom-Utf8Base64 "UmVsZWFzZSDpobXpnaLvvJo=") + $releasePage
$releaseInfoLines += ""
$releaseInfoLines += ConvertFrom-Utf8Base64 "6ZyA6KaB5LiK5Lyg77ya"
$releaseInfoLines += (ConvertFrom-Utf8Base64 "MS4g5a6M5pW05YyFIHppcO+8mg==") + (Split-Path -Leaf $zipFullPath)
$releaseInfoLines += (ConvertFrom-Utf8Base64 "QXBwUGF0Y2gg5YyF77ya") + $patchReleaseInfo
$releaseInfoLines += (ConvertFrom-Utf8Base64 "My4g5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
$releaseInfoLines += "4. launcher manifest: " + $launcherManifestName
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
}
$releaseInfo = $releaseInfoLines -join [Environment]::NewLine
$releaseInfo | Set-Content -LiteralPath $releaseInfoPath -Encoding UTF8

$zipParent = Split-Path -Parent $zipFullPath
if (-not [string]::IsNullOrWhiteSpace($zipParent)) {
    New-Item -ItemType Directory -Force -Path $zipParent | Out-Null
}
Compress-PackageWithRetry -SourceDir $outputFullPath -DestinationZip $zipFullPath

if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "ExpressPackingMonitoring.exe")) {
    throw "Full zip validation failed: missing root launcher"
}
if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/ExpressPackingMonitoring.exe")) {
    throw "Full zip validation failed: missing app/ExpressPackingMonitoring.exe"
}

Write-Host "Clean package created: $outputFullPath"
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
