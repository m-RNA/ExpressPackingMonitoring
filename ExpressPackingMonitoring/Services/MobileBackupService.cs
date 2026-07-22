using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileBackupService
{
    internal const string ProtocolVersion = "mobile-backup-v1";
    internal const int ChunkSizeBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan UploadRetention = TimeSpan.FromDays(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly VideoDatabase _database;
    private readonly string _stateDirectory;
    private readonly Func<string> _recordingRootResolver;
    private readonly Func<string, OrderInfo?> _orderInfoResolver;
    private readonly ConcurrentDictionary<string, object> _uploadLocks = new(StringComparer.OrdinalIgnoreCase);

    public MobileBackupService(
        VideoDatabase database,
        string stateDirectory,
        Func<string> recordingRootResolver,
        Func<string, OrderInfo?>? orderInfoResolver = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _stateDirectory = string.IsNullOrWhiteSpace(stateDirectory)
            ? throw new ArgumentException("上传状态目录不能为空", nameof(stateDirectory))
            : Path.GetFullPath(stateDirectory);
        _recordingRootResolver = recordingRootResolver
            ?? throw new ArgumentNullException(nameof(recordingRootResolver));
        _orderInfoResolver = orderInfoResolver ?? (_ => null);
        Directory.CreateDirectory(_stateDirectory);
        CleanupExpiredUploads();
    }

    public MobileBackupCreateResult CreateOrResume(MobileBackupCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sha256 = NormalizeSha256(request.FileSha256);
        ValidateTotalBytes(request.TotalBytes);
        ValidateMimeType(request.MimeType);
        string uploadId = sha256;

        lock (GetUploadLock(uploadId))
        {
            VideoRecord? existing = _database.GetVideoByContentSha256(sha256);
            if (existing != null && File.Exists(existing.FilePath))
            {
                long existingLength = new FileInfo(existing.FilePath).Length;
                if (existingLength == request.TotalBytes)
                    return new MobileBackupCreateResult(uploadId, existingLength, ChunkSizeBytes, true);
            }

            MobileBackupUploadState? state = LoadState(uploadId);
            if (state != null)
            {
                if (!string.Equals(state.FileSha256, sha256, StringComparison.OrdinalIgnoreCase)
                    || state.TotalBytes != request.TotalBytes
                    || !string.Equals(state.MimeType, request.MimeType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MobileBackupValidationException("upload_conflict", "同一上传任务的文件信息不一致");
                }

                if (TryUseStateFinalFile(state, out _, out long completedSize))
                    return new MobileBackupCreateResult(uploadId, completedSize, ChunkSizeBytes, true);

                long offset = File.Exists(PartPath(uploadId)) ? new FileInfo(PartPath(uploadId)).Length : 0;
                state.ReceivedBytes = offset;
                state.UpdatedAtUtc = DateTime.UtcNow;
                SaveState(state);
                return new MobileBackupCreateResult(uploadId, offset, ChunkSizeBytes, false);
            }

            state = new MobileBackupUploadState
            {
                UploadId = uploadId,
                FileSha256 = sha256,
                TotalBytes = request.TotalBytes,
                ReceivedBytes = 0,
                MimeType = request.MimeType.Trim().ToLowerInvariant(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            SaveState(state);
            return new MobileBackupCreateResult(uploadId, 0, ChunkSizeBytes, false);
        }
    }

    public long AppendChunk(
        string uploadId,
        long start,
        long end,
        long total,
        byte[] content,
        string chunkSha256)
    {
        uploadId = NormalizeSha256(uploadId);
        ArgumentNullException.ThrowIfNull(content);
        string normalizedChunkSha = NormalizeSha256(chunkSha256);

        lock (GetUploadLock(uploadId))
        {
            MobileBackupUploadState state = LoadState(uploadId)
                ?? throw new MobileBackupValidationException("upload_not_found", "上传任务不存在或已过期");
            if (total != state.TotalBytes || start < 0 || end < start || end >= total)
                throw new MobileBackupValidationException("invalid_content_range", "Content-Range 与上传任务不一致");
            long expectedLength = end - start + 1;
            if (expectedLength != content.LongLength || content.Length > ChunkSizeBytes)
                throw new MobileBackupValidationException("invalid_chunk_size", "分块长度不正确或超过服务端上限");

            string partPath = PartPath(uploadId);
            long expectedOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (start != expectedOffset)
                throw new MobileBackupOffsetException(expectedOffset);

            string actualChunkSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actualChunkSha, normalizedChunkSha, StringComparison.Ordinal))
                throw new MobileBackupValidationException("chunk_sha256_mismatch", "分块 SHA256 校验失败");

            using (var stream = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            state.ReceivedBytes = expectedOffset + content.Length;
            state.UpdatedAtUtc = DateTime.UtcNow;
            SaveState(state);
            return state.ReceivedBytes;
        }
    }

    public MobileBackupCompleteResult Complete(string uploadId, MobileBackupCompleteRequest request)
    {
        uploadId = NormalizeSha256(uploadId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateCompleteRequest(request);
        IReadOnlyList<MobileBackupSessionRequest> sessions = request.GetSessions();
        string fileSha256 = NormalizeSha256(request.FileSha256);
        if (!string.Equals(uploadId, fileSha256, StringComparison.Ordinal))
            throw new MobileBackupValidationException("upload_sha256_mismatch", "上传任务与完整文件 SHA256 不一致");

        lock (GetUploadLock(uploadId))
        {
            var existingRecords = sessions
                .Select(session => _database.GetVideoBySourceSession(request.SourceDeviceId, session.SessionId))
                .ToList();
            foreach (VideoRecord? completed in existingRecords.Where(record => record != null))
            {
                if (!string.Equals(completed!.ContentSha256, fileSha256, StringComparison.OrdinalIgnoreCase))
                    throw new MobileBackupValidationException("session_conflict", "该设备录像 ID 已绑定其他文件");
            }
            if (existingRecords.All(record => record != null))
            {
                long[] completedIds = existingRecords.Select(record => record!.Id).ToArray();
                return new MobileBackupCompleteResult("verified", fileSha256, completedIds[0], completedIds, true);
            }

            string finalPath;
            long fileSize;
            VideoRecord? existingFile = _database.GetVideoByContentSha256(fileSha256);
            if (existingFile != null && File.Exists(existingFile.FilePath))
            {
                finalPath = existingFile.FilePath;
                fileSize = new FileInfo(finalPath).Length;
            }
            else if (TryUseStateFinalFile(LoadState(uploadId), out finalPath, out fileSize))
            {
                // 文件已原子移动但数据库写入失败时，重试完成请求可继续落库。
            }
            else
            {
                MobileBackupUploadState state = LoadState(uploadId)
                    ?? throw new MobileBackupValidationException("upload_not_found", "上传任务不存在或已过期");
                string partPath = PartPath(uploadId);
                if (!File.Exists(partPath) || new FileInfo(partPath).Length != state.TotalBytes)
                    throw new MobileBackupOffsetException(File.Exists(partPath) ? new FileInfo(partPath).Length : 0);

                string actualSha256 = ComputeFileSha256(partPath);
                if (!string.Equals(actualSha256, fileSha256, StringComparison.Ordinal))
                {
                    ResetUpload(uploadId);
                    throw new MobileBackupFileHashException();
                }

                finalPath = ResolveFinalPath(
                    sessions,
                    fileSha256,
                    request.SourceDeviceId,
                    request.SourceDeviceName);
                state.FinalPath = finalPath;
                state.UpdatedAtUtc = DateTime.UtcNow;
                SaveState(state);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (File.Exists(finalPath))
                {
                    if (!string.Equals(ComputeFileSha256(finalPath), fileSha256, StringComparison.Ordinal))
                        throw new IOException("目标备份文件已存在但校验值不一致");
                    File.Delete(partPath);
                }
                else
                {
                    File.Move(partPath, finalPath);
                }
                fileSize = new FileInfo(finalPath).Length;
            }

            var recordIds = new List<long>(sessions.Count);
            for (int index = 0; index < sessions.Count; index++)
            {
                MobileBackupSessionRequest session = sessions[index];
                VideoRecord? existing = existingRecords[index];
                if (existing != null)
                {
                    recordIds.Add(existing.Id);
                    continue;
                }

                string trackingNumber = session.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
                OrderInfo? computerOrderInfo = string.IsNullOrEmpty(trackingNumber) ? null : _orderInfoResolver(trackingNumber);
                OrderInfo? orderInfo = MergeOrderInfo(computerOrderInfo, session.OrderInfo, trackingNumber);
                DateTime localStartTime = session.StartedAt.ToLocalTime().DateTime;
                recordIds.Add(_database.InsertMobileBackupRecord(
                    trackingNumber,
                    finalPath,
                    fileSize,
                    localStartTime,
                    session.DurationMilliseconds / 1000.0,
                    request.SourceDeviceId,
                    request.SourceDeviceName,
                    session.SessionId,
                    fileSha256,
                    orderInfo));
            }

            DeleteStateFile(uploadId);
            return new MobileBackupCompleteResult("verified", fileSha256, recordIds[0], recordIds, false);
        }
    }

    internal static OrderInfo? MergeOrderInfo(OrderInfo? computer, OrderInfo? mobile, string trackingNumber)
    {
        if (computer == null && mobile == null) return null;
        static string Prefer(string? primary, string? fallback) =>
            !string.IsNullOrWhiteSpace(primary) ? primary.Trim() : fallback?.Trim() ?? "";
        return new OrderInfo
        {
            TrackingNumber = Prefer(computer?.TrackingNumber, Prefer(mobile?.TrackingNumber, trackingNumber)).ToUpperInvariant(),
            OrderId = Prefer(computer?.OrderId, mobile?.OrderId),
            BuyerMessage = Prefer(computer?.BuyerMessage, mobile?.BuyerMessage),
            SellerMemo = Prefer(computer?.SellerMemo, mobile?.SellerMemo),
            ProductInfo = Prefer(computer?.ProductInfo, mobile?.ProductInfo),
            HasRefund = computer?.HasRefund == true || mobile?.HasRefund == true,
            IsPrintedRefund = computer?.IsPrintedRefund == true || mobile?.IsPrintedRefund == true,
            RefundStatus = Prefer(computer?.RefundStatus, mobile?.RefundStatus),
            RefundProductInfo = Prefer(computer?.RefundProductInfo, mobile?.RefundProductInfo),
            PushTime = new[] { computer?.PushTime, mobile?.PushTime }
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(DateTime.Now)
                .Max(),
            IsTest = false
        };
    }

    internal void CleanupExpiredUploads()
    {
        if (!Directory.Exists(_stateDirectory)) return;
        DateTime cutoff = DateTime.UtcNow - UploadRetention;
        foreach (string statePath in Directory.EnumerateFiles(_stateDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                MobileBackupUploadState? state = JsonSerializer.Deserialize<MobileBackupUploadState>(File.ReadAllText(statePath), JsonOptions);
                if (state == null || state.UpdatedAtUtc >= cutoff) continue;
                string uploadId = Path.GetFileNameWithoutExtension(statePath);
                lock (GetUploadLock(uploadId))
                    ResetUpload(uploadId);
            }
            catch
            {
                if (File.GetLastWriteTimeUtc(statePath) < cutoff)
                {
                    try { File.Delete(statePath); } catch { }
                }
            }
        }
    }

    private object GetUploadLock(string uploadId) => _uploadLocks.GetOrAdd(uploadId, _ => new object());

    private string StatePath(string uploadId) => Path.Combine(_stateDirectory, $"{uploadId}.json");

    private string PartPath(string uploadId) => Path.Combine(_stateDirectory, $"{uploadId}.part");

    private bool TryUseStateFinalFile(MobileBackupUploadState? state, out string finalPath, out long fileSize)
    {
        finalPath = state?.FinalPath ?? "";
        fileSize = 0;
        if (state == null || string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath)) return false;
        if (!string.Equals(ComputeFileSha256(finalPath), state.FileSha256, StringComparison.Ordinal))
            throw new IOException("目标备份文件已存在但校验值不一致");
        fileSize = new FileInfo(finalPath).Length;
        return true;
    }

    private string ResolveFinalPath(
        IReadOnlyList<MobileBackupSessionRequest> sessions,
        string fileSha256,
        string sourceDeviceId,
        string sourceDeviceName)
    {
        MobileBackupSessionRequest earliest = sessions.OrderBy(session => session.StartedAt).First();
        string trackingNumber = sessions
            .OrderBy(session => session.StartedAt)
            .Select(session => session.TrackingNumber?.Trim().ToUpperInvariant() ?? "")
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "未识别面单";
        DateTime startedAt = earliest.StartedAt.ToLocalTime().DateTime;
        string root = _recordingRootResolver()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(root))
            throw new IOException("电脑录像存储路径为空");

        string dateDirectory = Path.Combine(
            Path.GetFullPath(root),
            "手机备份",
            GetDeviceDirectoryName(sourceDeviceId, sourceDeviceName),
            startedAt.ToString("yyyy-MM-dd"));
        string baseName = SanitizeFileName($"{trackingNumber}_{startedAt:yyyyMMdd_HHmmss}_发货");
        string preferredPath = Path.Combine(dateDirectory, $"{baseName}.mp4");
        if (!File.Exists(preferredPath) || FileMatchesSha256(preferredPath, fileSha256))
            return preferredPath;

        string collisionPath = Path.Combine(dateDirectory, $"{baseName}_{fileSha256[..8]}.mp4");
        if (!File.Exists(collisionPath) || FileMatchesSha256(collisionPath, fileSha256))
            return collisionPath;
        throw new IOException("目标录像文件名冲突");
    }

    private static bool FileMatchesSha256(string path, string sha256) =>
        string.Equals(ComputeFileSha256(path), sha256, StringComparison.Ordinal);

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = value.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "未识别面单" : value;
    }

    internal static string GetDeviceDirectoryName(string sourceDeviceId, string sourceDeviceName)
    {
        string readableName = SanitizeFileName(sourceDeviceName ?? "");
        if (string.Equals(readableName, "未识别面单", StringComparison.Ordinal))
            readableName = "手机";
        if (readableName.Length > 32)
            readableName = readableName[..32].TrimEnd('.', ' ');

        string normalizedId = new((sourceDeviceId ?? "")
            .Where(char.IsLetterOrDigit)
            .ToArray());
        string shortId = normalizedId.Length switch
        {
            0 => "未知设备",
            <= 6 => normalizedId.ToUpperInvariant(),
            _ => normalizedId[^6..].ToUpperInvariant()
        };
        return $"{readableName}-{shortId}";
    }

    private MobileBackupUploadState? LoadState(string uploadId)
    {
        string path = StatePath(uploadId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MobileBackupUploadState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MobileBackupValidationException("upload_state_corrupt", $"上传任务状态损坏：{ex.Message}");
        }
    }

    private void SaveState(MobileBackupUploadState state)
    {
        Directory.CreateDirectory(_stateDirectory);
        string path = StatePath(state.UploadId);
        string tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private void ResetUpload(string uploadId)
    {
        TryDelete(PartPath(uploadId));
        DeleteStateFile(uploadId);
    }

    private void DeleteStateFile(string uploadId)
    {
        TryDelete(StatePath(uploadId));
        TryDelete($"{StatePath(uploadId)}.tmp");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new MobileBackupValidationException("invalid_sha256", "SHA256 必须是 64 位十六进制字符串");
        return normalized;
    }

    private static void ValidateTotalBytes(long totalBytes)
    {
        if (totalBytes <= 0)
            throw new MobileBackupValidationException("invalid_file_size", "文件大小必须大于 0");
    }

    private static void ValidateMimeType(string mimeType)
    {
        if (!string.Equals(mimeType?.Trim(), "video/mp4", StringComparison.OrdinalIgnoreCase))
            throw new MobileBackupValidationException("unsupported_format", "mobile-backup-v1 仅支持 video/mp4");
    }

    private static void ValidateCompleteRequest(MobileBackupCompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDeviceId) || request.SourceDeviceId.Trim().Length > 128)
            throw new MobileBackupValidationException("invalid_source_device_id", "来源设备 ID 不能为空且最多 128 个字符");
        if (string.IsNullOrWhiteSpace(request.SourceDeviceName) || request.SourceDeviceName.Trim().Length > 100)
            throw new MobileBackupValidationException("invalid_source_device_name", "来源设备名称不能为空且最多 100 个字符");
        IReadOnlyList<MobileBackupSessionRequest> sessions = request.GetSessions();
        if (sessions.Count == 0 || sessions.Count > 500)
            throw new MobileBackupValidationException("invalid_sessions", "录像片段数量必须在 1 到 500 之间");
        foreach (MobileBackupSessionRequest session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.SessionId) || session.SessionId.Trim().Length > 128)
                throw new MobileBackupValidationException("invalid_session_id", "sessionId 不能为空且最多 128 个字符");
            if ((session.TrackingNumber?.Trim().Length ?? 0) > 100)
                throw new MobileBackupValidationException("invalid_tracking_number", "面单号最多 100 个字符");
            if (session.StartedAt == default)
                throw new MobileBackupValidationException("invalid_started_at", "startedAt 不能为空");
            if (session.DurationMilliseconds <= 0 || session.DurationMilliseconds > TimeSpan.FromDays(2).TotalMilliseconds)
                throw new MobileBackupValidationException("invalid_duration", "录像时长必须大于 0 且不超过 48 小时");
            if (session.OrderInfo != null)
            {
                WebServer.ValidateOrderInfoItems(new List<OrderInfo> { session.OrderInfo });
                string sessionTracking = session.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
                string orderTracking = session.OrderInfo.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
                if (!string.IsNullOrEmpty(orderTracking)
                    && !string.IsNullOrEmpty(sessionTracking)
                    && !string.Equals(orderTracking, sessionTracking, StringComparison.Ordinal))
                    throw new MobileBackupValidationException("order_tracking_mismatch", "订单快照与录像面单号不一致");
            }
        }
    }
}

internal sealed class MobileBackupCreateRequest
{
    public string FileSha256 { get; set; } = "";
    public long TotalBytes { get; set; }
    public string MimeType { get; set; } = "";
}

internal sealed class MobileBackupCompleteRequest
{
    public string FileSha256 { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public long DurationMilliseconds { get; set; }
    public string SourceDeviceId { get; set; } = "";
    public string SourceDeviceName { get; set; } = "";
    public List<MobileBackupSessionRequest> Sessions { get; set; } = new();

    public IReadOnlyList<MobileBackupSessionRequest> GetSessions()
    {
        if (Sessions.Count > 0) return Sessions;
        return new[]
        {
            new MobileBackupSessionRequest
            {
                SessionId = SessionId,
                TrackingNumber = TrackingNumber,
                StartedAt = StartedAt,
                DurationMilliseconds = DurationMilliseconds
            }
        };
    }
}

internal sealed class MobileBackupSessionRequest
{
    public string SessionId { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public long DurationMilliseconds { get; set; }
    public OrderInfo? OrderInfo { get; set; }
}

internal sealed record MobileBackupCreateResult(string UploadId, long Offset, int ChunkSize, bool FileReady);

internal sealed record MobileBackupCompleteResult(
    string Status,
    string FileSha256,
    long RecordId,
    IReadOnlyList<long> RecordIds,
    bool AlreadyCompleted);

internal sealed class MobileBackupUploadState
{
    public string UploadId { get; set; } = "";
    public string FileSha256 { get; set; } = "";
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public string MimeType { get; set; } = "";
    public string FinalPath { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class MobileBackupValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

internal sealed class MobileBackupOffsetException(long expectedOffset) : Exception("上传偏移与服务端不一致")
{
    public long ExpectedOffset { get; } = expectedOffset;
}

internal sealed class MobileBackupFileHashException() : Exception("完整文件 SHA256 校验失败");
