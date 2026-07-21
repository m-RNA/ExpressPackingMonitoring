#requires -Version 7.2

[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "ExpressPackingMonitoring"),
    [string]$DatabasePath,
    [string]$LogDir,
    [string]$AppDir,
    [string]$OutputDir = (Join-Path ([Environment]::GetFolderPath("Desktop")) "录像缺失诊断")
)

$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param([string]$Path, [string]$Description)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        throw "$Description 不存在：$Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Find-AppDirectory {
    param([string]$ExplicitPath)

    $candidates = @(
        $ExplicitPath,
        (Join-Path $PSScriptRoot "app"),
        (Join-Path $PSScriptRoot "..\app"),
        (Join-Path $PSScriptRoot "..\ExpressPackingMonitoring\bin\Release\net8.0-windows")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate "Microsoft.Data.Sqlite.dll")) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "找不到 Microsoft.Data.Sqlite.dll。请用 -AppDir 指向正式包的 app 目录。"
}

function Import-SqliteRuntime {
    param([string]$RuntimeDir)

    foreach ($name in @(
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "Microsoft.Data.Sqlite.dll"
    )) {
        $assemblyPath = Join-Path $RuntimeDir $name
        if (-not (Test-Path -LiteralPath $assemblyPath)) {
            throw "SQLite 运行库不完整：$assemblyPath"
        }
        [System.Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
    }

    [SQLitePCL.Batteries_V2]::Init()
}

function Read-ActiveVideoRecords {
    param([string]$Path)

    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$Path;Mode=ReadOnly")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = @"
SELECT Id, OrderId, FilePath, FileSizeBytes, StartTime, EndTime, DurationSeconds
FROM VideoRecords
WHERE IsDeleted = 0
ORDER BY Id;
"@
        $reader = $command.ExecuteReader()
        $records = [System.Collections.Generic.List[object]]::new()
        while ($reader.Read()) {
            $records.Add([pscustomobject]@{
                RecordId = $reader.GetInt64(0)
                OrderId = if ($reader.IsDBNull(1)) { "" } else { $reader.GetString(1) }
                FilePath = if ($reader.IsDBNull(2)) { "" } else { $reader.GetString(2) }
                RecordedSizeBytes = if ($reader.IsDBNull(3)) { 0L } else { $reader.GetInt64(3) }
                StartTime = if ($reader.IsDBNull(4)) { "" } else { $reader.GetString(4) }
                EndTime = if ($reader.IsDBNull(5)) { "" } else { $reader.GetString(5) }
                DurationSeconds = if ($reader.IsDBNull(6)) { 0.0 } else { $reader.GetDouble(6) }
            })
        }
        return $records
    }
    finally {
        $connection.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $DatabasePath = Join-Path $DataDir "videos.db"
}
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $DataDir "log"
}

$DatabasePath = Resolve-ExistingPath $DatabasePath "视频数据库"
$AppDir = Find-AppDirectory $AppDir
Import-SqliteRuntime $AppDir

$convertedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$missingSourceNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$logFiles = @()
if (Test-Path -LiteralPath $LogDir) {
    $logFiles = @(Get-ChildItem -LiteralPath $LogDir -File -Filter "*.log" -ErrorAction SilentlyContinue)
    foreach ($logFile in $logFiles) {
        foreach ($line in [System.IO.File]::ReadLines($logFile.FullName, [Text.Encoding]::UTF8)) {
            if ($line -match '\[MkvToMp4\] Converted file=([^,]+\.mkv),') {
                [void]$convertedNames.Add($Matches[1])
            }
            elseif ($line -match '\[MkvRecover\] Convert failed file=([^,]+\.mkv), error=MKV 文件不存在') {
                [void]$missingSourceNames.Add($Matches[1])
            }
        }
    }
}

$records = @(Read-ActiveVideoRecords $DatabasePath)
$missing = foreach ($record in $records) {
    if ([string]::IsNullOrWhiteSpace($record.FilePath) -or (Test-Path -LiteralPath $record.FilePath -PathType Leaf)) {
        continue
    }

    $extension = [IO.Path]::GetExtension($record.FilePath)
    $mkvName = [IO.Path]::GetFileName([IO.Path]::ChangeExtension($record.FilePath, ".mkv"))
    $sourceMkvPath = [IO.Path]::ChangeExtension($record.FilePath, ".mkv")
    $hasConcurrencyEvidence =
        $extension -ieq ".mp4" -and
        $convertedNames.Contains($mkvName) -and
        $missingSourceNames.Contains($mkvName)

    $root = [IO.Path]::GetPathRoot($record.FilePath)
    $driveAvailable = -not [string]::IsNullOrWhiteSpace($root) -and (Test-Path -LiteralPath $root)

    [pscustomobject]@{
        Classification = if (-not $driveAvailable) { "存储盘不可用" } elseif ($hasConcurrencyEvidence) { "高度疑似并发误删" } elseif (Test-Path -LiteralPath $sourceMkvPath -PathType Leaf) { "MP4缺失但MKV仍在" } else { "普通缺失，需人工核对" }
        RecordId = $record.RecordId
        OrderId = $record.OrderId
        StartTime = $record.StartTime
        EndTime = $record.EndTime
        DurationSeconds = $record.DurationSeconds
        RecordedSizeBytes = $record.RecordedSizeBytes
        FilePath = $record.FilePath
        SourceMkvExists = Test-Path -LiteralPath $sourceMkvPath -PathType Leaf
        ConversionSucceededInLog = $convertedNames.Contains($mkvName)
        MissingSourceInLog = $missingSourceNames.Contains($mkvName)
        DriveAvailable = $driveAvailable
    }
}

$uniqueMissing = @($missing | Group-Object FilePath | ForEach-Object { $_.Group[0] })
$suspected = @($uniqueMissing | Where-Object Classification -eq "高度疑似并发误删")
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$csvPath = Join-Path $OutputDir "missing-videos-$timestamp.csv"
$summaryPath = Join-Path $OutputDir "summary-$timestamp.txt"
$uniqueMissing | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8BOM

$summary = @(
    "录像缺失只读诊断",
    "时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "数据库：$DatabasePath",
    "日志目录：$LogDir",
    "扫描日志：$($logFiles.Count) 个",
    "有效数据库记录：$($records.Count) 条",
    "缺失文件：$($uniqueMissing.Count) 个",
    "高度疑似并发误删：$($suspected.Count) 个",
    "",
    "说明：高度疑似项同时满足文件缺失、日志记录转换成功、随后又记录 MKV 文件不存在。脚本不会修改数据库或文件。"
)
[IO.File]::WriteAllLines($summaryPath, $summary, [Text.UTF8Encoding]::new($true))

$summary | ForEach-Object { Write-Host $_ }
Write-Host "明细：$csvPath"
Write-Host "摘要：$summaryPath"
