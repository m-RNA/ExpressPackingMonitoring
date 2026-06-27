param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$launcherProject = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "ExpressPackingMonitoring\bin\Release\package\$Runtime"
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

function Invoke-DotNetPublish {
    dotnet publish @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
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

$appPublishDir = Join-Path $outputFullPath "app"
$appBaseOutput = Join-Path $repoRoot "ExpressPackingMonitoring\bin_publish_tmp\clean-package-app\"
$appBaseIntermediate = Join-Path $repoRoot "ExpressPackingMonitoring\obj_publish_tmp\clean-package-app\"
$launcherBaseOutput = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\bin_publish_tmp\clean-package-launcher\"
$launcherBaseIntermediate = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\obj_publish_tmp\clean-package-launcher\"

Invoke-DotNetPublish $appProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
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

$zipParent = Split-Path -Parent $zipFullPath
if (-not [string]::IsNullOrWhiteSpace($zipParent)) {
    New-Item -ItemType Directory -Force -Path $zipParent | Out-Null
}
Get-ChildItem -LiteralPath $outputFullPath -Force |
    Compress-Archive -DestinationPath $zipFullPath -CompressionLevel Optimal -Force

Write-Host "Clean package created: $outputFullPath"
Write-Host "Zip package created: $zipFullPath"
Write-Host "Root items:"
Get-ChildItem -LiteralPath $outputFullPath | Sort-Object PSIsContainer, Name | Select-Object Name, Mode, Length | Format-Table -AutoSize
