param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "ExpressPackingMonitoring.sln"
$testsProject = Join-Path $repoRoot "ExpressPackingMonitoring.Tests\ExpressPackingMonitoring.Tests.csproj"
$webPage = Join-Path $repoRoot "ExpressPackingMonitoring\Web\index.html"

$requiredCoreTests = @(
    "CameraLifecycleTests.PreviewSessionGate_StaleCallbackCannotReleaseAwakenedSession",
    "CameraLifecycleTests.CameraFrameReadySignal_WakeRequiresNewSessionFrame",
    "CameraLifecycleTests.CameraFrameReadySignal_RecordingStartTimesOutWithoutFrame",
    "ConfigurationAndScannerTests.IsFastSequence_DistinguishesScannerAndManualTyping",
    "ConfigurationAndScannerTests.ShouldAlertPrintedRefund_AlertsEnabledShippingAndReturnScans",
    "ConfigurationAndScannerTests.ShouldAlertPrintedRefund_UsesRefundStatus",
    "ConfigurationAndScannerTests.GetPrintedRefundLookupDelay_RequestsImmediatelyThenThrottlesForFiveSeconds",
    "ConfigurationAndScannerTests.RefundWorkerUserscript_IsolatesLookupFromUserPage",
    "ConfigurationAndScannerTests.AddMonitorConnectPermission_AddsExactHostWithoutDuplicatingWildcardPermission",
    "ConfigurationAndScannerTests.AlertService_CriticalAlertBlocksNormalAlertUntilDisplayEnds",
    "ConfigurationAndScannerTests.AlertService_ForwardsIndustrialAlarmOnceAndRefundSpeechThreeTimes",
    "VideoDatabaseTests.GetRecentCompletedVideos_ReturnsLatestTwentyValidRecordsForDate",
    "VideoDatabaseTests.OrderIdExistsRecent_ChecksThirtyDaysAndIgnoresDeletedOrExcludedRecords",
    "VideoDatabaseTests.GetRecentOrderInfos_UsesDatabaseAsNinetyDaySourceOfTruth",
    "VideoDatabaseTests.UpsertOrderInfos_DoesNotLetOlderSnapshotOverwriteNewerRefundState",
    "VideoDatabaseTests.VideoRecords_DerivesFileNameFromPathAndDoesNotPersistRedundantColumn",
    "WebRequestLimitTests.ClipEditor_UsesSingleScreenSourcePlaybackWorkflow"
)

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $testsProject)) {
    throw "Test project not found: $testsProject"
}
if (-not (Test-Path $webPage)) {
    throw "Web page not found: $webPage"
}

Write-Host "Discovering required core business and recovery tests..."
$testList = (& dotnet test $testsProject -c $Configuration --nologo --list-tests 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Test discovery failed with exit code $LASTEXITCODE`n$testList"
}
foreach ($requiredTest in $requiredCoreTests) {
    if (-not $testList.Contains($requiredTest, [System.StringComparison]::Ordinal)) {
        throw "Required release test is missing: $requiredTest"
    }
}

Write-Host "Running complete release test suite..."
Invoke-DotNet -Arguments @("test", $testsProject, "-c", $Configuration, "--nologo", "--no-restore")

Write-Host "Checking Web JavaScript syntax..."
$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $node) {
    throw "Node.js is required for release Web JavaScript validation."
}
$html = [System.IO.File]::ReadAllText($webPage, [System.Text.Encoding]::UTF8)
$scriptStart = $html.IndexOf("<script>", [System.StringComparison]::Ordinal)
$scriptEnd = $html.LastIndexOf("</script>", [System.StringComparison]::Ordinal)
if ($scriptStart -lt 0 -or $scriptEnd -le $scriptStart) {
    throw "Web page script block was not found."
}
$scriptStart += "<script>".Length
$tempScript = Join-Path ([System.IO.Path]::GetTempPath()) "ExpressPackingMonitoring-release-$([Guid]::NewGuid().ToString('N')).js"
try {
    [System.IO.File]::WriteAllText(
        $tempScript,
        $html.Substring($scriptStart, $scriptEnd - $scriptStart),
        [System.Text.UTF8Encoding]::new($false))
    & $node.Source --check $tempScript
    if ($LASTEXITCODE -ne 0) {
        throw "Web JavaScript syntax validation failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
}

Write-Host "Building complete release solution..."
Invoke-DotNet -Arguments @("build", $solution, "-c", $Configuration, "--nologo", "--no-restore")

Write-Host "Release automated validation passed."
