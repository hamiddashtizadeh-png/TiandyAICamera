using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// پایگاه داده محلی مستقل SQLite برای ثبت و بازیابی رویدادهای پلاک‌خوانی ANPR
/// مجهز به ژورنالینگ WAL، تراکنش‌های امن و شاخص‌گذاری پرسرعت روی پلاک و تاریخ
/// </summary>
public sealed class AnprArchiveDatabase : IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _isDisposed;

    public string DatabasePath => _dbPath;

    public AnprArchiveDatabase(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5000
        };
        _connectionString = csb.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS AnprEvents (
                    EventId TEXT PRIMARY KEY,
                    CameraId TEXT NOT NULL,
                    ChannelIndex INTEGER NOT NULL,
                    TimestampUtc TEXT NOT NULL,
                    Part1 TEXT NOT NULL,
                    Letter TEXT NOT NULL,
                    Part2 TEXT NOT NULL,
                    IranCode TEXT NOT NULL,
                    PlateType TEXT NOT NULL,
                    NormalizedPlate TEXT NOT NULL,
                    CompactPlate TEXT NOT NULL,
                    RawOcrText TEXT NOT NULL,
                    DetectionConfidence REAL NOT NULL,
                    OcrConfidence REAL NOT NULL,
                    ValidationStatus TEXT NOT NULL,
                    VehicleBoxJson TEXT,
                    PlateBoxJson TEXT,
                    EncryptedVehicleCropPath TEXT,
                    EncryptedPlateCropPath TEXT,
                    InferenceDurationMs REAL NOT NULL,
                    ModelInfo TEXT NOT NULL,
                    ExecutionProvider TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_AnprEvents_CompactPlate ON AnprEvents(CompactPlate);
                CREATE INDEX IF NOT EXISTS IX_AnprEvents_Camera_Time ON AnprEvents(CameraId, TimestampUtc);
                CREATE INDEX IF NOT EXISTS IX_AnprEvents_IranCode ON AnprEvents(IranCode);
                CREATE INDEX IF NOT EXISTS IX_AnprEvents_Status ON AnprEvents(ValidationStatus);
            ";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// درج رویداد جدید پلاک‌خوانی به صورت اتمیک
    /// </summary>
    public async Task InsertEventAsync(AnprResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));

        await _writeGate.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AnprEvents (
                    EventId, CameraId, ChannelIndex, TimestampUtc,
                    Part1, Letter, Part2, IranCode, PlateType,
                    NormalizedPlate, CompactPlate, RawOcrText,
                    DetectionConfidence, OcrConfidence, ValidationStatus,
                    VehicleBoxJson, PlateBoxJson,
                    EncryptedVehicleCropPath, EncryptedPlateCropPath,
                    InferenceDurationMs, ModelInfo, ExecutionProvider, CreatedAtUtc
                ) VALUES (
                    $EventId, $CameraId, $ChannelIndex, $TimestampUtc,
                    $Part1, $Letter, $Part2, $IranCode, $PlateType,
                    $NormalizedPlate, $CompactPlate, $RawOcrText,
                    $DetectionConfidence, $OcrConfidence, $ValidationStatus,
                    $VehicleBoxJson, $PlateBoxJson,
                    $EncryptedVehicleCropPath, $EncryptedPlateCropPath,
                    $InferenceDurationMs, $ModelInfo, $ExecutionProvider, $CreatedAtUtc
                );
            ";

            cmd.Parameters.AddWithValue("$EventId", result.EventId.ToString("D"));
            cmd.Parameters.AddWithValue("$CameraId", result.CameraId);
            cmd.Parameters.AddWithValue("$ChannelIndex", result.ChannelIndex);
            cmd.Parameters.AddWithValue("$TimestampUtc", result.TimestampUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$Part1", result.Plate.Part1);
            cmd.Parameters.AddWithValue("$Letter", result.Plate.LetterText);
            cmd.Parameters.AddWithValue("$Part2", result.Plate.Part2);
            cmd.Parameters.AddWithValue("$IranCode", result.Plate.IranCode);
            cmd.Parameters.AddWithValue("$PlateType", result.Plate.PlateType.ToString());
            cmd.Parameters.AddWithValue("$NormalizedPlate", result.Plate.NormalizedText);
            cmd.Parameters.AddWithValue("$CompactPlate", result.Plate.CompactText);
            cmd.Parameters.AddWithValue("$RawOcrText", result.Plate.RawOcr);
            cmd.Parameters.AddWithValue("$DetectionConfidence", result.PlateDetectionConfidence);
            cmd.Parameters.AddWithValue("$OcrConfidence", result.OcrConfidence);
            cmd.Parameters.AddWithValue("$ValidationStatus", result.Plate.ValidationStatus.ToString());
            cmd.Parameters.AddWithValue("$VehicleBoxJson", result.VehicleBox != null ? $"{result.VehicleBox.X:F4},{result.VehicleBox.Y:F4},{result.VehicleBox.Width:F4},{result.VehicleBox.Height:F4}" : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$PlateBoxJson", result.PlateBox != null ? $"{result.PlateBox.X:F4},{result.PlateBox.Y:F4},{result.PlateBox.Width:F4},{result.PlateBox.Height:F4}" : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$EncryptedVehicleCropPath", (object?)result.EncryptedVehicleCropPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$EncryptedPlateCropPath", (object?)result.EncryptedPlateCropPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$InferenceDurationMs", result.InferenceDurationMs);
            cmd.Parameters.AddWithValue("$ModelInfo", result.ModelInfo);
            cmd.Parameters.AddWithValue("$ExecutionProvider", result.ExecutionProvider);
            cmd.Parameters.AddWithValue("$CreatedAtUtc", DateTime.UtcNow.ToString("o"));

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// جستجوی منعطف رویدادهای پلاک‌خوانی با فیلتر متن پلاک (کامل یا بخشی)، شناسه دوربین، بازه تاریخی و وضعیت
    /// </summary>
    public async Task<List<AnprResult>> SearchEventsAsync(
        string? plateQuery = null,
        string? cameraId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        AnprValidationStatus? status = null,
        int limit = 100)
    {
        var results = new List<AnprResult>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var query = @"
            SELECT
                EventId, CameraId, ChannelIndex, TimestampUtc,
                Part1, Letter, Part2, IranCode, PlateType,
                NormalizedPlate, CompactPlate, RawOcrText,
                DetectionConfidence, OcrConfidence, ValidationStatus,
                VehicleBoxJson, PlateBoxJson,
                EncryptedVehicleCropPath, EncryptedPlateCropPath,
                InferenceDurationMs, ModelInfo, ExecutionProvider
            FROM AnprEvents
            WHERE 1=1
        ";

        await using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(cameraId))
        {
            query += " AND CameraId = $CameraId";
            cmd.Parameters.AddWithValue("$CameraId", cameraId);
        }

        if (fromUtc.HasValue)
        {
            query += " AND TimestampUtc >= $FromUtc";
            cmd.Parameters.AddWithValue("$FromUtc", fromUtc.Value.ToString("o"));
        }

        if (toUtc.HasValue)
        {
            query += " AND TimestampUtc <= $ToUtc";
            cmd.Parameters.AddWithValue("$ToUtc", toUtc.Value.ToString("o"));
        }

        if (status.HasValue)
        {
            query += " AND ValidationStatus = $Status";
            cmd.Parameters.AddWithValue("$Status", status.Value.ToString());
        }

        if (!string.IsNullOrWhiteSpace(plateQuery))
        {
            string clean = IranianPlateNormalizer.ToLatinDigits(plateQuery.Trim())
                .Replace(" ", "")
                .Replace("-", "");
            query += " AND (CompactPlate LIKE $PlateLike OR RawOcrText LIKE $PlateLike OR NormalizedPlate LIKE $PlateFaLike)";
            cmd.Parameters.AddWithValue("$PlateLike", $"%{clean}%");
            cmd.Parameters.AddWithValue("$PlateFaLike", $"%{plateQuery.Trim()}%");
        }

        query += " ORDER BY TimestampUtc DESC LIMIT $Limit;";
        cmd.Parameters.AddWithValue("$Limit", Math.Max(1, limit));
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string eventIdStr = reader.GetString(0);
            Guid eventId = Guid.TryParse(eventIdStr, out var gid) ? gid : Guid.NewGuid();
            string cam = reader.GetString(1);
            int ch = reader.GetInt32(2);
            DateTime ts = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);

            string part1 = reader.GetString(4);
            string letter = reader.GetString(5);
            string part2 = reader.GetString(6);
            string iranCode = reader.GetString(7);
            string typeStr = reader.GetString(8);
            string normPlate = reader.GetString(9);
            string compactPlate = reader.GetString(10);
            string rawOcr = reader.GetString(11);
            float detConf = (float)reader.GetDouble(12);
            float ocrConf = (float)reader.GetDouble(13);
            string statusStr = reader.GetString(14);

            string? vehBox = reader.IsDBNull(15) ? null : reader.GetString(15);
            string? pltBox = reader.IsDBNull(16) ? null : reader.GetString(16);
            string? encVeh = reader.IsDBNull(17) ? null : reader.GetString(17);
            string? encPlt = reader.IsDBNull(18) ? null : reader.GetString(18);
            double duration = reader.GetDouble(19);
            string model = reader.GetString(20);
            string ep = reader.GetString(21);

            Enum.TryParse<IranianPlateType>(typeStr, true, out var plateType);
            Enum.TryParse<AnprValidationStatus>(statusStr, true, out var valStatus);

            var plate = new IranianPlate
            {
                Part1 = part1,
                LetterText = letter,
                Part2 = part2,
                IranCode = iranCode,
                PlateType = plateType,
                NormalizedText = normPlate,
                CompactText = compactPlate,
                RawOcr = rawOcr,
                IsValid = valStatus == AnprValidationStatus.Verified,
                ValidationStatus = valStatus
            };

            results.Add(new AnprResult
            {
                EventId = eventId,
                CameraId = cam,
                ChannelIndex = ch,
                TimestampUtc = ts,
                Plate = plate,
                PlateDetectionConfidence = detConf,
                OcrConfidence = ocrConf,
                EncryptedVehicleCropPath = encVeh,
                EncryptedPlateCropPath = encPlt,
                InferenceDurationMs = duration,
                ModelInfo = model,
                ExecutionProvider = ep
            });
        }

        return results;
    }

    /// <summary>
    /// حذف مستقل رویدادهای مشخص‌شده از پایگاه داده
    /// </summary>
    public async Task<int> DeleteEventsAsync(IEnumerable<Guid> eventIds)
    {
        if (eventIds == null) return 0;

        await _writeGate.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            int deletedCount = 0;
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "DELETE FROM AnprEvents WHERE EventId = $EventId;";
            var pId = cmd.Parameters.Add("$EventId", SqliteType.Text);

            foreach (var id in eventIds)
            {
                pId.Value = id.ToString("D");
                deletedCount += await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return deletedCount;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// آمار کل رویدادهای پلاک‌خوانی
    /// </summary>
    public async Task<(int TotalEvents, int VerifiedEvents, int TodayEvents, double AvgConfidence)> GetStatisticsAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN ValidationStatus = 'Verified' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN date(TimestampUtc) = date('now') THEN 1 ELSE 0 END), 0),
                COALESCE(AVG(OcrConfidence), 0.0)
            FROM AnprEvents;
        ";

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            int total = reader.GetInt32(0);
            int verified = reader.GetInt32(1);
            int today = reader.GetInt32(2);
            double avgConf = reader.GetDouble(3);
            return (total, verified, today, avgConf);
        }

        return (0, 0, 0, 0.0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _writeGate.Dispose();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _writeGate.Dispose();
    }
}
