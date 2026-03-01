#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// 视频录制记录
    /// </summary>
    public class VideoRecord
    {
        public long Id { get; set; }
        public string OrderId { get; set; } = "";
        public string Mode { get; set; } = "";       // 发货/退货
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationSeconds { get; set; }
        public string StopReason { get; set; } = "";  // 手动/静止超时/时长超时/程序退出
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string DeleteReason { get; set; } = ""; // 磁盘清理/手动删除
    }

    /// <summary>
    /// 每日统计记录（从数据库聚合）
    /// </summary>
    public class DailyStat
    {
        public string Date { get; set; } = "";
        public int TotalPieces { get; set; }
        public double TotalDurationSec { get; set; }
    }

    /// <summary>
    /// 删除日志记录
    /// </summary>
    public class DeleteLogEntry
    {
        public long Id { get; set; }
        public string FilePath { get; set; } = "";
        public string OrderId { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public DateTime DeletedAt { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 本地 SQLite 视频数据库，统一管理录制记录、统计数据和删除日志。
    /// 替代原来的 daily_stats.json 和文件系统扫描。
    /// </summary>
    public class VideoDatabase : IDisposable
    {
        private readonly string _dbPath;
        private SqliteConnection _connection;
        private readonly object _lock = new object();

        public VideoDatabase(string dbPath)
        {
            _dbPath = dbPath;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // 启用 WAL 模式提高并发性能
            ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            ExecuteNonQuery("PRAGMA synchronous=NORMAL;");

            // 视频录制表
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS VideoRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId TEXT NOT NULL,
                    Mode TEXT NOT NULL DEFAULT '',
                    FilePath TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    FileSizeBytes INTEGER DEFAULT 0,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    DurationSeconds REAL DEFAULT 0,
                    StopReason TEXT DEFAULT '',
                    IsDeleted INTEGER DEFAULT 0,
                    DeletedAt TEXT,
                    DeleteReason TEXT DEFAULT ''
                );");

            // 删除日志表
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS DeleteLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL,
                    OrderId TEXT DEFAULT '',
                    FileSizeBytes INTEGER DEFAULT 0,
                    DeletedAt TEXT NOT NULL,
                    Reason TEXT DEFAULT ''
                );");

            // 索引
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_orderid ON VideoRecords(OrderId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_starttime ON VideoRecords(StartTime);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_filepath ON VideoRecords(FilePath);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_isdeleted ON VideoRecords(IsDeleted);");
        }

        /// <summary>
        /// 录制开始时插入记录，返回记录 ID
        /// </summary>
        public long InsertVideoRecord(string orderId, string mode, string filePath, DateTime startTime)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO VideoRecords (OrderId, Mode, FilePath, FileName, StartTime)
                    VALUES (@orderId, @mode, @filePath, @fileName, @startTime);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@orderId", orderId ?? "");
                cmd.Parameters.AddWithValue("@mode", mode ?? "");
                cmd.Parameters.AddWithValue("@filePath", filePath ?? "");
                cmd.Parameters.AddWithValue("@fileName", Path.GetFileName(filePath ?? ""));
                cmd.Parameters.AddWithValue("@startTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return (long)cmd.ExecuteScalar();
            }
        }

        /// <summary>
        /// 录制结束时更新记录
        /// </summary>
        public void UpdateVideoRecordOnStop(long recordId, DateTime endTime, double durationSeconds, long fileSizeBytes, string stopReason)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords SET 
                        EndTime = @endTime, 
                        DurationSeconds = @duration, 
                        FileSizeBytes = @fileSize,
                        StopReason = @stopReason
                    WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@endTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@duration", durationSeconds);
                cmd.Parameters.AddWithValue("@fileSize", fileSizeBytes);
                cmd.Parameters.AddWithValue("@stopReason", stopReason ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 标记视频为已删除并写入删除日志
        /// </summary>
        public void MarkVideoDeleted(string filePath, string reason)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                try
                {
                    // 更新视频记录
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            UPDATE VideoRecords SET 
                                IsDeleted = 1, DeletedAt = @deletedAt, DeleteReason = @reason
                            WHERE FilePath = @filePath AND IsDeleted = 0;";
                        cmd.Parameters.AddWithValue("@filePath", filePath);
                        cmd.Parameters.AddWithValue("@deletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@reason", reason ?? "");
                        cmd.ExecuteNonQuery();
                    }

                    // 写入删除日志
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT INTO DeleteLogs (FilePath, OrderId, FileSizeBytes, DeletedAt, Reason)
                            SELECT FilePath, OrderId, FileSizeBytes, @deletedAt, @reason
                            FROM VideoRecords WHERE FilePath = @filePath
                            LIMIT 1;";
                        cmd.Parameters.AddWithValue("@filePath", filePath);
                        cmd.Parameters.AddWithValue("@deletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@reason", reason ?? "");
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                }
            }
        }

        /// <summary>
        /// 查询视频列表（支持日期范围 + 关键词过滤）
        /// </summary>
        public List<VideoRecord> QueryVideos(DateTime startDate, DateTime endDate, string keyword = null)
        {
            lock (_lock)
            {
                var results = new List<VideoRecord>();
                using var cmd = _connection.CreateCommand();

                string sql = @"
                    SELECT Id, OrderId, Mode, FilePath, FileName, FileSizeBytes, 
                           StartTime, EndTime, DurationSeconds, StopReason
                    FROM VideoRecords 
                    WHERE IsDeleted = 0 
                      AND StartTime >= @startDate 
                      AND StartTime < @endDate";

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    sql += " AND (OrderId LIKE @keyword OR FileName LIKE @keyword)";
                    cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");
                }

                sql += " ORDER BY StartTime DESC;";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@startDate", startDate.ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@endDate", endDate.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        Mode = reader.GetString(2),
                        FilePath = reader.GetString(3),
                        FileName = reader.GetString(4),
                        FileSizeBytes = reader.GetInt64(5),
                        StartTime = DateTime.Parse(reader.GetString(6)),
                        EndTime = reader.IsDBNull(7) ? DateTime.MinValue : DateTime.Parse(reader.GetString(7)),
                        DurationSeconds = reader.GetDouble(8),
                        StopReason = reader.IsDBNull(9) ? "" : reader.GetString(9)
                    });
                }
                return results;
            }
        }

        /// <summary>
        /// 获取每日统计数据（替代 daily_stats.json）
        /// </summary>
        public List<DailyStat> GetDailyStats(int days = 7)
        {
            lock (_lock)
            {
                var results = new List<DailyStat>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        substr(StartTime, 1, 10) AS Date,
                        COUNT(*) AS TotalPieces,
                        SUM(DurationSeconds) AS TotalDurationSec
                    FROM VideoRecords 
                    WHERE IsDeleted = 0 
                      AND DurationSeconds > 0
                      AND EndTime IS NOT NULL
                      AND StartTime >= @startDate
                    GROUP BY substr(StartTime, 1, 10)
                    ORDER BY Date;";
                cmd.Parameters.AddWithValue("@startDate", DateTime.Now.AddDays(-days + 1).ToString("yyyy-MM-dd 00:00:00"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DailyStat
                    {
                        Date = reader.GetString(0),
                        TotalPieces = reader.GetInt32(1),
                        TotalDurationSec = reader.GetDouble(2)
                    });
                }
                return results;
            }
        }

        /// <summary>
        /// 获取今日统计（用于主界面显示）
        /// </summary>
        public DailyStat GetTodayStat()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*), COALESCE(SUM(DurationSeconds), 0)
                    FROM VideoRecords 
                    WHERE IsDeleted = 0 
                      AND DurationSeconds > 0
                      AND EndTime IS NOT NULL
                      AND substr(StartTime, 1, 10) = @today;";
                cmd.Parameters.AddWithValue("@today", DateTime.Now.ToString("yyyy-MM-dd"));

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new DailyStat
                    {
                        Date = DateTime.Now.ToString("yyyy-MM-dd"),
                        TotalPieces = reader.GetInt32(0),
                        TotalDurationSec = reader.GetDouble(1)
                    };
                }
                return new DailyStat { Date = DateTime.Now.ToString("yyyy-MM-dd") };
            }
        }

        /// <summary>
        /// 获取所有未删除视频的总磁盘占用
        /// </summary>
        public long GetTotalFileSizeBytes()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(SUM(FileSizeBytes), 0) FROM VideoRecords WHERE IsDeleted = 0;";
                return (long)cmd.ExecuteScalar();
            }
        }

        /// <summary>
        /// 按时间升序获取最旧的未删除视频（用于磁盘清理）
        /// </summary>
        public List<VideoRecord> GetOldestVideos(int limit = 100)
        {
            lock (_lock)
            {
                var results = new List<VideoRecord>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, FilePath, FileSizeBytes, StartTime
                    FROM VideoRecords 
                    WHERE IsDeleted = 0 
                    ORDER BY StartTime ASC 
                    LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        FilePath = reader.GetString(2),
                        FileSizeBytes = reader.GetInt64(3),
                        StartTime = DateTime.Parse(reader.GetString(4))
                    });
                }
                return results;
            }
        }

        /// <summary>
        /// 获取删除日志
        /// </summary>
        public List<DeleteLogEntry> GetDeleteLogs(int limit = 100)
        {
            lock (_lock)
            {
                var results = new List<DeleteLogEntry>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, FilePath, OrderId, FileSizeBytes, DeletedAt, Reason
                    FROM DeleteLogs ORDER BY DeletedAt DESC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DeleteLogEntry
                    {
                        Id = reader.GetInt64(0),
                        FilePath = reader.GetString(1),
                        OrderId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        FileSizeBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                        DeletedAt = DateTime.Parse(reader.GetString(4)),
                        Reason = reader.IsDBNull(5) ? "" : reader.GetString(5)
                    });
                }
                return results;
            }
        }



        private void ExecuteNonQuery(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { _connection?.Close(); _connection?.Dispose(); } catch { }
        }
    }
}
