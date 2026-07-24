using ExpressPackingMonitoring.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class UninstallRecordingPlan
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public List<UninstallRecordingPlanFile> Files { get; set; } = new();
}

internal sealed class UninstallRecordingPlanFile
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
}

internal sealed record UninstallCleanupResult(
    bool Success,
    int ProcessedFiles,
    long ProcessedBytes,
    string Message);

internal static class UninstallCleanupService
{
    private const string PlanOption = "--uninstall-plan-recordings";
    private const string DeleteOption = "--uninstall-delete-recordings";
    private const string LogOption = "--uninstall-log";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        string planPath = GetOptionValue(args, PlanOption);
        string deletePlanPath = GetOptionValue(args, DeleteOption);
        if (string.IsNullOrWhiteSpace(planPath) && string.IsNullOrWhiteSpace(deletePlanPath))
            return false;

        string logPath = GetOptionValue(args, LogOption);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            logPath = Path.Combine(
                Path.GetTempPath(),
                $"ExpressPackingMonitoring-Uninstall-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }

        string userDataDirectory = GetUserDataDirectory();
        string databasePath = Path.Combine(userDataDirectory, "videos.db");
        try
        {
            if (WorkstationInstanceCoordinator.IsRoleRunning(WorkstationRoles.CameraMonitor) ||
                WorkstationInstanceCoordinator.IsRoleRunning(WorkstationRoles.PrintStation))
            {
                throw new InvalidOperationException("快递打包监控仍在运行，请关闭所有程序窗口后重试");
            }

            UninstallCleanupResult result = !string.IsNullOrWhiteSpace(planPath)
                ? CreateRecordingPlan(databasePath, planPath, logPath)
                : ExecuteRecordingPlan(databasePath, deletePlanPath, logPath);
            AppendLog(logPath, result.Message);
            exitCode = result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppendLog(logPath, $"Uninstall cleanup command failed: {ex}");
            exitCode = 1;
        }

        return true;
    }

    internal static UninstallCleanupResult CreateRecordingPlan(
        string databasePath,
        string planPath,
        string logPath)
    {
        string normalizedDatabasePath = NormalizeExistingFile(databasePath, "录像数据库不存在");
        string normalizedPlanPath = NormalizeOutputFile(planPath);
        HashSet<string> registeredPaths = ReadRegisteredPaths(normalizedDatabasePath);
        var plannedFiles = new List<UninstallRecordingPlanFile>();

        foreach (string registeredPath in registeredPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(registeredPath);
            file.Refresh();
            if (!file.Exists)
                continue;

            plannedFiles.Add(new UninstallRecordingPlanFile
            {
                Path = file.FullName,
                Length = file.Length,
                LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks
            });
        }

        var plan = new UninstallRecordingPlan
        {
            CreatedAtUtc = DateTime.UtcNow,
            TotalFiles = plannedFiles.Count,
            TotalBytes = plannedFiles.Sum(file => file.Length),
            Files = plannedFiles
        };

        string? planDirectory = Path.GetDirectoryName(normalizedPlanPath);
        if (string.IsNullOrWhiteSpace(planDirectory))
            throw new InvalidOperationException("无法确定卸载清单目录");
        Directory.CreateDirectory(planDirectory);
        string temporaryPath = normalizedPlanPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(plan, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, normalizedPlanPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }

        string message = $"Recording cleanup plan created: files={plan.TotalFiles}, bytes={plan.TotalBytes}";
        AppendLog(logPath, message);
        return new UninstallCleanupResult(true, plan.TotalFiles, plan.TotalBytes, message);
    }

    internal static UninstallCleanupResult ExecuteRecordingPlan(
        string databasePath,
        string planPath,
        string logPath)
    {
        string normalizedDatabasePath = NormalizeExistingFile(databasePath, "录像数据库不存在");
        string normalizedPlanPath = NormalizeExistingFile(planPath, "卸载录像清单不存在");
        UninstallRecordingPlan plan = JsonSerializer.Deserialize<UninstallRecordingPlan>(
            File.ReadAllText(normalizedPlanPath, Encoding.UTF8),
            JsonOptions) ?? throw new InvalidDataException("卸载录像清单无效");
        if (plan.Version != 1 || plan.Files == null)
            throw new InvalidDataException("不支持的卸载录像清单版本");
        long calculatedBytes;
        try
        {
            calculatedBytes = checked(plan.Files.Sum(file => file.Length));
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("卸载录像清单容量无效", ex);
        }
        if (plan.TotalFiles != plan.Files.Count ||
            plan.TotalBytes != calculatedBytes ||
            plan.Files.Any(file => file.Length < 0 || file.LastWriteTimeUtcTicks <= 0))
        {
            throw new InvalidDataException("卸载录像清单汇总与文件明细不一致");
        }

        HashSet<string> registeredPaths = ReadRegisteredPaths(normalizedDatabasePath);
        var uniquePlanPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        int deletedFiles = 0;
        long deletedBytes = 0;

        using var connection = OpenDatabase(normalizedDatabasePath, SqliteOpenMode.ReadWrite);
        ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");

        foreach (UninstallRecordingPlanFile plannedFile in plan.Files)
        {
            string normalizedPath;
            try
            {
                normalizedPath = NormalizeRootedPath(plannedFile.Path);
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
                continue;
            }

            if (!uniquePlanPaths.Add(normalizedPath))
            {
                failures.Add($"清单包含重复路径：{normalizedPath}");
                continue;
            }
            if (!registeredPaths.Contains(normalizedPath))
            {
                failures.Add($"数据库未登记该文件：{normalizedPath}");
                continue;
            }

            var currentFile = new FileInfo(normalizedPath);
            currentFile.Refresh();
            if (!currentFile.Exists)
            {
                failures.Add($"录像文件已不存在：{normalizedPath}");
                continue;
            }
            if (currentFile.Length != plannedFile.Length ||
                currentFile.LastWriteTimeUtc.Ticks != plannedFile.LastWriteTimeUtcTicks)
            {
                failures.Add($"录像文件在确认后发生变化，已保留：{normalizedPath}");
                continue;
            }

            try
            {
                AppendLog(logPath, $"Deleting registered recording: {normalizedPath}");
                File.Delete(normalizedPath);
                if (File.Exists(normalizedPath))
                    throw new IOException("文件删除后仍然存在");

                RecordDatabaseDeletion(connection, normalizedPath);
                deletedFiles++;
                deletedBytes += plannedFile.Length;
                AppendLog(logPath, $"Deleted registered recording: {normalizedPath}");
            }
            catch (Exception ex)
            {
                failures.Add($"删除失败：{normalizedPath}，{ex.Message}");
                AppendLog(logPath, $"Recording deletion failed: path={normalizedPath}, error={ex}");
            }
        }

        bool success = failures.Count == 0;
        string message = success
            ? $"Recording cleanup completed: files={deletedFiles}, bytes={deletedBytes}"
            : $"Recording cleanup incomplete: deleted={deletedFiles}, failures={failures.Count}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
        AppendLog(logPath, message);
        return new UninstallCleanupResult(success, deletedFiles, deletedBytes, message);
    }

    private static HashSet<string> ReadRegisteredPaths(string databasePath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenDatabase(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT DISTINCT FilePath
            FROM VideoRecords
            WHERE FilePath IS NOT NULL AND TRIM(FilePath) <> '';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(NormalizeRootedPath(reader.GetString(0)));
        return paths;
    }

    private static SqliteConnection OpenDatabase(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void RecordDatabaseDeletion(SqliteConnection connection, string filePath)
    {
        using var transaction = connection.BeginTransaction();
        string deletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        const string reason = "卸载时删除";
        try
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
                    UPDATE VideoRecords
                    SET IsDeleted = 1, DeletedAt = @deletedAt, DeleteReason = @reason
                    WHERE FilePath = @filePath AND IsDeleted = 0;";
                update.Parameters.AddWithValue("@filePath", filePath);
                update.Parameters.AddWithValue("@deletedAt", deletedAt);
                update.Parameters.AddWithValue("@reason", reason);
                update.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"
                    INSERT INTO DeleteLogs (FilePath, OrderId, FileSizeBytes, DeletedAt, Reason)
                    SELECT FilePath, OrderId, FileSizeBytes, @deletedAt, @reason
                    FROM VideoRecords
                    WHERE FilePath = @filePath
                    LIMIT 1;";
                insert.Parameters.AddWithValue("@filePath", filePath);
                insert.Parameters.AddWithValue("@deletedAt", deletedAt);
                insert.Parameters.AddWithValue("@reason", reason);
                if (insert.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("数据库删除日志写入失败");
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string NormalizeExistingFile(string path, string missingMessage)
    {
        string normalized = NormalizeRootedPath(path);
        if (!File.Exists(normalized))
            throw new FileNotFoundException(missingMessage, normalized);
        return normalized;
    }

    private static string NormalizeOutputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("卸载清单路径不能为空", nameof(path));
        return NormalizeRootedPath(path);
    }

    private static string NormalizeRootedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new InvalidDataException($"路径无效或不是绝对路径：{path}");
        return Path.GetFullPath(path);
    }

    private static string GetUserDataDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("EPM_USER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new DirectoryNotFoundException("无法定位本机用户数据目录");
        return Path.Combine(localAppData, "ExpressPackingMonitoring");
    }

    private static string GetOptionValue(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index] ?? "";
            if (argument.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                return argument[(optionName.Length + 1)..].Trim().Trim('"');
            if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return (args[index + 1] ?? "").Trim().Trim('"');
        }
        return "";
    }

    private static void AppendLog(string logPath, string message)
    {
        try
        {
            string normalizedLogPath = Path.GetFullPath(logPath);
            string? directory = Path.GetDirectoryName(normalizedLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(
                normalizedLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
