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
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
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
        public long TotalBytes { get; set; } // 新增
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

            EnsureColumnExists("VideoRecords", "VideoCodec", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "VideoEncoder", "TEXT DEFAULT ''");
        }

        /// <summary>
        /// 录制开始时插入记录，返回记录 ID
        /// </summary>
        public long InsertVideoRecord(string orderId, string mode, string videoCodec, string videoEncoder, string filePath, DateTime startTime)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO VideoRecords (OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileName, StartTime)
                    VALUES (@orderId, @mode, @videoCodec, @videoEncoder, @filePath, @fileName, @startTime);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@orderId", orderId ?? "");
                cmd.Parameters.AddWithValue("@mode", mode ?? "");
                cmd.Parameters.AddWithValue("@videoCodec", videoCodec ?? "");
                cmd.Parameters.AddWithValue("@videoEncoder", videoEncoder ?? "");
                cmd.Parameters.AddWithValue("@filePath", filePath ?? "");
                cmd.Parameters.AddWithValue("@fileName", Path.GetFileName(filePath ?? ""));
                cmd.Parameters.AddWithValue("@startTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return (long)cmd.ExecuteScalar();
            }
        }

        /// <summary>
        /// 录制结束时更新记录
        /// </summary>
        public void UpdateVideoRecordOnStop(long recordId, DateTime endTime, double durationSeconds, long fileSizeBytes, string stopReason, string videoCodec = null, string videoEncoder = null)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords SET 
                        EndTime = @endTime, 
                        DurationSeconds = @duration, 
                        FileSizeBytes = @fileSize,
                        StopReason = @stopReason,
                        VideoCodec = COALESCE(@videoCodec, VideoCodec),
                        VideoEncoder = COALESCE(@videoEncoder, VideoEncoder)
                    WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@endTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@duration", durationSeconds);
                cmd.Parameters.AddWithValue("@fileSize", fileSizeBytes);
                cmd.Parameters.AddWithValue("@stopReason", stopReason ?? "");
                cmd.Parameters.AddWithValue("@videoCodec", (object)videoCodec ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@videoEncoder", (object)videoEncoder ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateVideoFileSize(long recordId, long fileSizeBytes)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "UPDATE VideoRecords SET FileSizeBytes = @fileSize WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@fileSize", fileSizeBytes);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 更新视频记录的文件路径（MKV 转 MP4 后调用）
        /// </summary>
        public void UpdateVideoFilePath(string oldPath, string newPath)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords SET 
                        FilePath = @newPath,
                        FileName = @newFileName
                    WHERE FilePath = @oldPath;";
                cmd.Parameters.AddWithValue("@oldPath", oldPath);
                cmd.Parameters.AddWithValue("@newPath", newPath);
                cmd.Parameters.AddWithValue("@newFileName", Path.GetFileName(newPath));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 查询所有未删除且文件路径以 .mkv 结尾的记录
        /// </summary>
        public List<string> QueryMkvFilePaths()
        {
            lock (_lock)
            {
                var paths = new List<string>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT FilePath FROM VideoRecords WHERE IsDeleted = 0 AND FilePath LIKE '%.mkv';";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    paths.Add(reader.GetString(0));
                }
                return paths;
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
        /// 检查今日是否已存在指定单号的未删除录像记录
        /// </summary>
        public bool OrderIdExistsToday(string orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId)) return false;
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(1) FROM VideoRecords
                    WHERE OrderId = @orderId
                      AND StartTime >= @today
                      AND IsDeleted = 0
                      AND DurationSeconds > 0;";
                cmd.Parameters.AddWithValue("@orderId", orderId);
                cmd.Parameters.AddWithValue("@today", DateTime.Now.ToString("yyyy-MM-dd 00:00:00"));
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>
        /// 查询视频列表（支持日期范围 + 关键词过滤，包含已删除记录）
        /// </summary>
        public VideoRecord GetVideoById(long id)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileName, FileSizeBytes, 
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason
                    FROM VideoRecords WHERE Id = @id AND IsDeleted = 0;";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        Mode = reader.GetString(2),
                        VideoCodec = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        VideoEncoder = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        FilePath = reader.GetString(5),
                        FileName = reader.GetString(6),
                        FileSizeBytes = reader.GetInt64(7),
                        StartTime = DateTime.Parse(reader.GetString(8)),
                        EndTime = reader.IsDBNull(9) ? DateTime.MinValue : DateTime.Parse(reader.GetString(9)),
                        DurationSeconds = reader.GetDouble(10),
                        StopReason = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        IsDeleted = reader.GetInt64(12) == 1,
                        DeletedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                        DeleteReason = reader.IsDBNull(14) ? "" : reader.GetString(14)
                    };
                }
                return null;
            }
        }

        /// <summary>
        /// 查询视频列表（支持日期范围 + 关键词过滤，包含已删除记录）
        /// </summary>
        public List<VideoRecord> QueryVideos(DateTime startDate, DateTime endDate, string keyword = null)
        {
            lock (_lock)
            {
                var results = new List<VideoRecord>();
                using var cmd = _connection.CreateCommand();

                string sql = @"
                      SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileName, FileSizeBytes, 
                          StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason
                    FROM VideoRecords 
                    WHERE StartTime >= @startDate 
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
                        VideoCodec = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        VideoEncoder = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        FilePath = reader.GetString(5),
                        FileName = reader.GetString(6),
                        FileSizeBytes = reader.GetInt64(7),
                        StartTime = DateTime.Parse(reader.GetString(8)),
                        EndTime = reader.IsDBNull(9) ? DateTime.MinValue : DateTime.Parse(reader.GetString(9)),
                        DurationSeconds = reader.GetDouble(10),
                        StopReason = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        IsDeleted = reader.GetInt64(12) == 1,
                        DeletedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                        DeleteReason = reader.IsDBNull(14) ? "" : reader.GetString(14)
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
            return GetRangeStats(DateTime.Now.AddDays(-days + 1), DateTime.Now);
        }

        /// <summary>
        /// 增加对时间段范围聚合统计（支持文件大小）
        /// </summary>
        public List<DailyStat> GetRangeStats(DateTime start, DateTime end)
        {
            lock (_lock)
            {
                var results = new List<DailyStat>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        substr(StartTime, 1, 10) AS Date,
                        COUNT(*) AS TotalPieces,
                        SUM(DurationSeconds) AS TotalDurationSec,
                        SUM(FileSizeBytes) AS TotalBytes
                    FROM VideoRecords 
                    WHERE StartTime >= @start AND StartTime <= @end
                      AND IsDeleted = 0 
                      AND EndTime IS NOT NULL
                    GROUP BY substr(StartTime, 1, 10)
                    ORDER BY Date ASC;";
                cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd 23:59:59"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DailyStat
                    {
                        Date = reader.GetString(0),
                        TotalPieces = reader.GetInt32(1),
                        TotalDurationSec = reader.GetDouble(2),
                        TotalBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
                    });
                }
                return results;
            }
        }

        public List<DailyStat> GetAggregatedStats(DateTime start, DateTime end, string groupBy = "day")
        {
            lock (_lock)
            {
                var results = new List<DailyStat>();
                using var cmd = _connection.CreateCommand();

                string dateSelector = groupBy switch
                {
                    "week" => "strftime('%Y-W%W', StartTime)",
                    "month" => "strftime('%Y-%m', StartTime)",
                    _ => "substr(StartTime, 1, 10)"
                };

                cmd.CommandText = $@"
                    SELECT 
                        {dateSelector} AS GroupDate,
                        COUNT(*) AS TotalPieces,
                        SUM(DurationSeconds) AS TotalDurationSec,
                        SUM(FileSizeBytes) AS TotalBytes
                    FROM VideoRecords 
                    WHERE StartTime >= @start AND StartTime <= @end
                      AND IsDeleted = 0 AND EndTime IS NOT NULL
                    GROUP BY GroupDate
                    ORDER BY GroupDate ASC;";

                cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd 23:59:59"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DailyStat
                    {
                        Date = reader.GetString(0),
                        TotalPieces = reader.GetInt32(1),
                        TotalDurationSec = reader.GetDouble(2),
                        TotalBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
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
        /// 获取所有未删除视频的总大小和总时长（用于估算可录制时长）
        /// </summary>
        public (long TotalBytes, double TotalDurationSec) GetGlobalSizeAndDuration()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"SELECT COALESCE(SUM(FileSizeBytes), 0), COALESCE(SUM(DurationSeconds), 0) 
                                    FROM VideoRecords WHERE IsDeleted = 0 AND DurationSeconds > 0 AND EndTime IS NOT NULL;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    return (reader.GetInt64(0), reader.GetDouble(1));
                return (0, 0);
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

        private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            ExecuteNonQuery($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        }

        public void Dispose()
        {
            try { _connection?.Close(); _connection?.Dispose(); } catch { }
        }
    }
}
