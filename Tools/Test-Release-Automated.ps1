param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $repoRoot "TestResults\Automation"
$runRoot = Join-Path $resultsRoot ([DateTime]::Now.ToString("yyyyMMdd-HHmmss"))
$dataRoot = Join-Path $runRoot "data"
$fixtureVideo = Join-Path $runRoot "fixture.mp4"
$hostStdout = Join-Path $runRoot "host.stdout.log"
$hostStderr = Join-Path $runRoot "host.stderr.log"
$releaseTest = Join-Path $repoRoot "Tools\Test-Release.ps1"
$buildArtifacts = Join-Path $repoRoot "TestResults\ReleaseBuild\$Configuration"
$configurationSegment = $Configuration.ToLowerInvariant()
$hostProcess = $null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-ForWebServer {
    param([string]$Url, [int]$TimeoutSeconds = 20)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $details = if (Test-Path $hostStderr) { [System.IO.File]::ReadAllText($hostStderr, [System.Text.Encoding]::UTF8) } else { "" }
    throw "Automation Web server did not become ready at $Url`n$details"
}

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path $runRoot, $dataRoot | Out-Null

    Write-Host "[1/7] Running release unit tests, JavaScript validation and Release build..."
    Invoke-Checked -FilePath "pwsh" -Arguments @("-NoProfile", "-File", $releaseTest, "-Configuration", $Configuration)
    Invoke-Checked -FilePath "node" -Arguments @("--check", "Scripts\快递助手订单推送.user.js")

    Write-Host "[2/7] Running an isolated WPF startup and shutdown smoke test..."
    $wpfDataRoot = Join-Path $runRoot "wpf-data"
    New-Item -ItemType Directory -Force -Path $wpfDataRoot | Out-Null
    $appExecutable = Join-Path $buildArtifacts "bin\ExpressPackingMonitoring\$($configurationSegment)_win-x64\ExpressPackingMonitoring.exe"
    if (-not (Test-Path $appExecutable)) { throw "Built WPF executable not found: $appExecutable" }
    $previousUserDataDir = $env:EPM_USER_DATA_DIR
    $previousInstanceScope = $env:EPM_INSTANCE_SCOPE
    $env:EPM_USER_DATA_DIR = $wpfDataRoot
    $env:EPM_INSTANCE_SCOPE = "automation$PID"
    $wpfProcess = $null
    try {
        $orderConfig = @{
            UnifiedModulesMigrationVersion = 1
            GlobalOnboardingVersion = 1
            OrderIntegrationSetupVersion = 1
            EnableOrderIntegration = $true
            EnableGlobalKeyboard = $false
            Language = "zh-Hans"
        } | ConvertTo-Json
        [System.IO.File]::WriteAllText(
            (Join-Path $wpfDataRoot "config.json"),
            $orderConfig,
            [System.Text.UTF8Encoding]::new($false))
        $wpfProcess = Start-Process -FilePath $appExecutable -ArgumentList @("--print-station") -PassThru -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 200
            $wpfProcess.Refresh()
            if ($wpfProcess.HasExited) { throw "The isolated WPF process exited before showing its main window." }
        } while ($wpfProcess.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
        if ($wpfProcess.MainWindowHandle -eq 0) { throw "The isolated WPF main window did not appear." }
        $titleDeadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            $wpfProcess.Refresh()
        } while (-not $wpfProcess.MainWindowTitle.StartsWith("快递打包监控", [System.StringComparison]::Ordinal) -and
                 [DateTime]::UtcNow -lt $titleDeadline)
        if (-not $wpfProcess.MainWindowTitle.StartsWith("快递打包监控", [System.StringComparison]::Ordinal)) {
            throw "Unexpected WPF window title: $($wpfProcess.MainWindowTitle)"
        }
        $wpfProcess.CloseMainWindow() | Out-Null
        if (-not $wpfProcess.WaitForExit(5000)) { throw "The isolated WPF process did not shut down cleanly." }

        $cameraConfig = @{
            UnifiedModulesMigrationVersion = 1
            GlobalOnboardingVersion = 1
            PcRecordingSetupVersion = 1
            EnablePcCameraRecording = $true
            CameraBarcodeSetupVersion = 1
            MobileConnectionSetupVersion = 1
            EnableCameraBarcodeRecognition = $false
            EnableGlobalKeyboard = $false
            Language = "zh-Hans"
        } | ConvertTo-Json
        [System.IO.File]::WriteAllText(
            (Join-Path $wpfDataRoot "config.json"),
            $cameraConfig,
            [System.Text.UTF8Encoding]::new($false))

        $env:EPM_INSTANCE_SCOPE = "automation-camera$PID"
        $wpfProcess = Start-Process -FilePath $appExecutable -ArgumentList @("--monitor") -PassThru -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 200
            $wpfProcess.Refresh()
            if ($wpfProcess.HasExited) { throw "The isolated camera-monitor process exited before showing MainWindow." }
        } while ($wpfProcess.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
        if ($wpfProcess.MainWindowHandle -eq 0) { throw "The isolated camera-monitor MainWindow did not appear." }
        $titleDeadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            $wpfProcess.Refresh()
        } while (-not $wpfProcess.MainWindowTitle.StartsWith("快递打包监控", [System.StringComparison]::Ordinal) -and
                 [DateTime]::UtcNow -lt $titleDeadline)
        if (-not $wpfProcess.MainWindowTitle.StartsWith("快递打包监控", [System.StringComparison]::Ordinal)) {
            throw "Unexpected camera-monitor window title: $($wpfProcess.MainWindowTitle)"
        }
        Stop-Process -Id $wpfProcess.Id -Force
        if (-not $wpfProcess.WaitForExit(5000)) { throw "The isolated camera-monitor process did not exit after the smoke test." }
    }
    finally {
        if ($wpfProcess -and -not $wpfProcess.HasExited) { Stop-Process -Id $wpfProcess.Id -Force -ErrorAction SilentlyContinue }
        $env:EPM_USER_DATA_DIR = $previousUserDataDir
        $env:EPM_INSTANCE_SCOPE = $previousInstanceScope
    }

    Write-Host "[3/7] Restoring the pinned Playwright test dependency..."
    Invoke-Checked -FilePath "npm" -Arguments @("ci", "--ignore-scripts")

    Write-Host "[4/7] Creating an isolated video fixture..."
    $ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source
    if (-not $ffmpeg) { $ffmpeg = Join-Path $repoRoot "ffmpeg.exe" }
    if (-not (Test-Path $ffmpeg)) { throw "ffmpeg is required for automated Web playback testing." }
    Invoke-Checked -FilePath $ffmpeg -Arguments @(
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=black:s=640x360:d=2",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", $fixtureVideo)

    Write-Host "[5/7] Starting an isolated monitor Web server..."
    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port/"
    $hostExecutable = Join-Path $buildArtifacts "bin\ExpressPackingMonitoring.AutomationHost\$($configurationSegment)_win-x64\ExpressPackingMonitoring.AutomationHost.exe"
    if (-not (Test-Path $hostExecutable)) { throw "Built automation host not found: $hostExecutable" }
    $hostArguments = @("$port", $dataRoot, $fixtureVideo)
    $hostProcess = Start-Process -FilePath $hostExecutable -ArgumentList $hostArguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $hostStdout -RedirectStandardError $hostStderr
    Wait-ForWebServer -Url $baseUrl

    Write-Host "[6/7] Running userscript concurrency/routing and headless Web UI tests..."
    $previousBaseUrl = $env:EPM_AUTOMATION_BASE_URL
    $previousBrowserExecutable = $env:EPM_BROWSER_EXECUTABLE
    $browserCandidates = @(
        (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe"),
        (Join-Path $env:LOCALAPPDATA "Google\Chrome\Application\chrome.exe"),
        (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe")
    )
    $browserExecutable = $browserCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $browserExecutable) { throw "Chrome or Microsoft Edge is required for automated Web UI testing." }
    $env:EPM_AUTOMATION_BASE_URL = $baseUrl
    $env:EPM_BROWSER_EXECUTABLE = $browserExecutable
    try { Invoke-Checked -FilePath "npm" -Arguments @("run", "test:e2e") }
    finally {
        $env:EPM_AUTOMATION_BASE_URL = $previousBaseUrl
        $env:EPM_BROWSER_EXECUTABLE = $previousBrowserExecutable
    }

    Write-Host "[7/7] Verifying test data isolation..."
    if (-not (Test-Path (Join-Path $dataRoot "automation.db"))) {
        throw "The isolated automation database was not created."
    }

    Write-Host ""
    Write-Host "Automated release acceptance passed."
    Write-Host "Artifacts: $runRoot"
    Write-Host "Still manual: physical camera/scanner disconnects, audible quality, and real-store refund truth."
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        $hostProcess.WaitForExit(5000) | Out-Null
    }
    Pop-Location
}
