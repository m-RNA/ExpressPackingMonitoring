param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $repoRoot "Installer\ExpressPackingMonitoring.iss"
$sourceFullPath = [System.IO.Path]::GetFullPath($SourceDir)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$normalizedVersion = $Version.Trim().TrimStart("vV".ToCharArray())
if ($normalizedVersion -notmatch "^\d+\.\d+\.\d+(?:\.\d+)?$") {
    throw "Installer version must contain 3 or 4 numeric parts: $Version"
}
$versionParts = @($normalizedVersion.Split("."))
$version4 = if ($versionParts.Count -eq 3) { "$normalizedVersion.0" } else { $normalizedVersion }
$setupFileName = "ExpressPackingMonitoring_Setup_v$normalizedVersion.exe"
$setupPath = Join-Path $outputFullPath $setupFileName

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "Inno Setup script not found: $installerScript"
}
foreach ($requiredPath in @(
    (Join-Path $sourceFullPath "ExpressPackingMonitoring.exe"),
    (Join-Path $sourceFullPath "app\ExpressPackingMonitoring.exe"),
    (Join-Path $sourceFullPath "app\ExpressPackingMonitoring.dll")
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Installer source is incomplete: $requiredPath"
    }
}

$forbiddenFiles = @(Get-ChildItem -LiteralPath $sourceFullPath -Recurse -File | Where-Object {
    $_.Name -ieq "config.json" -or
    $_.Name -like "videos.db*" -or
    $_.Name -ieq ".env" -or
    $_.Extension -ieq ".log"
})
if ($forbiddenFiles.Count -gt 0) {
    throw "Installer source contains runtime state: $($forbiddenFiles[0].FullName)"
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $IsccPath = $env:INNO_SETUP_ISCC
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $IsccPath = $command.Source
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $IsccPath = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "Inno Setup 6.7.3 compiler was not found. Install it with: winget install --id JRSoftware.InnoSetup -e -s winget"
}

New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null
if (Test-Path -LiteralPath $setupPath) {
    Remove-Item -LiteralPath $setupPath -Force
}

$temporaryBuildRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ("ExpressPackingMonitoring-Installer-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryBuildRoot | Out-Null
$temporarySetupPath = Join-Path $temporaryBuildRoot $setupFileName

$compilerArguments = @(
    "/Qp",
    "/DMyAppVersion=$normalizedVersion",
    "/DMyAppVersion4=$version4",
    "/DSourceDir=$sourceFullPath",
    "/DOutputDir=$temporaryBuildRoot"
)

$certificateThumbprint = ($env:WINDOWS_SIGN_CERT_THUMBPRINT ?? "").Trim()
if (-not [string]::IsNullOrWhiteSpace($certificateThumbprint)) {
    $signToolPath = ($env:WINDOWS_SIGNTOOL_PATH ?? "").Trim()
    if ([string]::IsNullOrWhiteSpace($signToolPath) -or -not (Test-Path -LiteralPath $signToolPath -PathType Leaf)) {
        throw "WINDOWS_SIGN_CERT_THUMBPRINT is set, but WINDOWS_SIGNTOOL_PATH does not point to signtool.exe"
    }
    $timestampUrl = ($env:WINDOWS_SIGN_TIMESTAMP_URL ?? "http://timestamp.digicert.com").Trim()
    $signToolDefinition =
        "`"$signToolPath`" sign /sha1 `"$certificateThumbprint`" /fd SHA256 /td SHA256 /tr `"$timestampUrl`" /d `"ExpressPackingMonitoring`" `$f"
    $compilerArguments += "/DSignToolName=epm_authenticode"
    $compilerArguments += "/Sepm_authenticode=$signToolDefinition"
}
else {
    Write-Warning "Installer will be unsigned. Windows SmartScreen may show an unknown publisher warning."
}

try {
    & $IsccPath @compilerArguments $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $temporarySetupPath -PathType Leaf)) {
        throw "Inno Setup did not create the expected installer: $temporarySetupPath"
    }

    $copied = $false
    for ($attempt = 1; $attempt -le 3 -and -not $copied; $attempt++) {
        try {
            Copy-Item -LiteralPath $temporarySetupPath -Destination $setupPath -Force
            $copied = $true
        }
        catch {
            if ($attempt -eq 3) {
                throw
            }
            Start-Sleep -Seconds 1
        }
    }
}
finally {
    $normalizedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $normalizedBuildRoot = [System.IO.Path]::GetFullPath($temporaryBuildRoot)
    if ($normalizedBuildRoot.StartsWith($normalizedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $normalizedBuildRoot)) {
        try {
            Remove-Item -LiteralPath $normalizedBuildRoot -Recurse -Force
        }
        catch {
            Write-Warning "Temporary installer directory could not be removed: $normalizedBuildRoot"
        }
    }
}

if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer copy failed: $setupPath"
}

$signature = Get-AuthenticodeSignature -LiteralPath $setupPath
Write-Host "Installer created: $setupPath"
Write-Host "Installer signature status: $($signature.Status)"
Write-Output $setupPath
