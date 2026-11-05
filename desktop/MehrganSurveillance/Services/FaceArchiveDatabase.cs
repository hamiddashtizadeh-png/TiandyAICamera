using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MehrganSurveillance.Services;

using MehrganSurveillance.Models;

/// <summary>
/// مدیریت پایگاه داده محلی SQLite برای آرشیو رویدادها و برش‌های چهره با پشتیبانی از WAL و تراکنش‌های امن
/// </summary>
public sealed class FaceArchiveDatabase : IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _isDisposed;

    public FaceArchiveDatabase(string dbPath)
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

                CREATE TABLE IF NOT EXISTS FaceEvents (
                    EventId TEXT PRIMARY KEY,
                    CameraId TEXT NOT NULL,
                    ChannelIndex INTEGER NOT NULL,
                    TimestampUtc TEXT NOT NULL,
                    FaceCount INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS FaceCrops (
                    CropId TEXT PRIMARY KEY,
                    EventId TEXT NOT NULL,
                    CropIndex INTEGER NOT NULL,
                    Confidence REAL NOT NULL,
                    NormX REAL NOT NULL,
                    NormY REAL NOT NULL,
                    NormW REAL NOT NULL,
                    NormH REAL NOT NULL,
                    EncryptedFilePath TEXT NOT NULL,
                    IsPendingDeletion INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(EventId) REFERENCES FaceEvents(EventId) ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS IX_FaceEvents_Camera_Time ON FaceEvents(CameraId, TimestampUtc);
                CREATE INDEX IF NOT EXISTS IX_FaceCrops_Event ON FaceCrops(EventId);
                CREATE INDEX IF NOT EXISTS IX_FaceCrops_Pending ON FaceCrops(IsPendingDeletion) WHERE IsPendingDeletion = 1;
            ";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task InsertEventWithCropsAsync(FaceEventRecord ev, List<FaceCropRecord> crops)
    {
        if (ev == null) throw new ArgumentNullException(nameof(ev));
        if (crops == null) throw new ArgumentNullException(nameof(crops));

        await _writeGate.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = @"
                    INSERT INTO FaceEvents (EventId, CameraId, ChannelIndex, TimestampUtc, FaceCount, CreatedAtUtc)
                    VALUES (@eventId, @camId, @chan, @time, @count, @created);
                ";
                cmd.Parameters.AddWithValue("@eventId", ev.EventId.ToString("N"));
                cmd.Parameters.AddWithValue("@camId", ev.CameraId);
                cmd.Parameters.AddWithValue("@chan", ev.ChannelIndex);
                cmd.Parameters.AddWithValue("@time", ev.TimestampUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@count", ev.FaceCount);
                cmd.Parameters.AddWithValue("@created", ev.CreatedAtUtc.ToString("o"));
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var crop in crops)
            {
                await using var cropCmd = conn.CreateCommand();
                cropCmd.Transaction = (SqliteTransaction)tx;
                cropCmd.CommandText = @"
                    INSERT INTO FaceCrops (CropId, EventId, CropIndex, Confidence, NormX, NormY, NormW, NormH, EncryptedFilePath, IsPendingDeletion)
                    VALUES (@cropId, @eventId, @index, @conf, @x, @y, @w, @h, @path, 0);
                ";
                cropCmd.Parameters.AddWithValue("@cropId", crop.CropId.ToString("N"));
                cropCmd.Parameters.AddWithValue("@eventId", ev.EventId.ToString("N"));
                cropCmd.Parameters.AddWithValue("@index", crop.CropIndex);
                cropCmd.Parameters.AddWithValue("@conf", crop.Confidence);
                cropCmd.Parameters.AddWithValue("@x", crop.NormX);
                cropCmd.Parameters.AddWithValue("@y", crop.NormY);
                cropCmd.Parameters.AddWithValue("@w", crop.NormW);
                cropCmd.Parameters.AddWithValue("@h", crop.NormH);
                cropCmd.Parameters.AddWithValue("@path", crop.EncryptedFilePath);
                await cropCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<List<FaceEventRecord>> QueryEventsAsync(FaceArchiveFilter filter)
    {
        var results = new List<FaceEventRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        string sql = @"
            SELECT EventId, CameraId, ChannelIndex, TimestampUtc, FaceCount, CreatedAtUtc
            FROM FaceEvents
            WHERE 1=1
        ";

        if (filter.FromUtc.HasValue) sql += " AND TimestampUtc >= @fromUtc";
        if (filter.ToUtc.HasValue) sql += " AND TimestampUtc <= @toUtc";
        if (!string.IsNullOrEmpty(filter.CameraId)) sql += " AND CameraId = @camId";

        sql += " ORDER BY TimestampUtc DESC LIMIT @limit OFFSET @offset;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (filter.FromUtc.HasValue) cmd.Parameters.AddWithValue("@fromUtc", filter.FromUtc.Value.ToString("o"));
        if (filter.ToUtc.HasValue) cmd.Parameters.AddWithValue("@toUtc", filter.ToUtc.Value.ToString("o"));
        if (!string.IsNullOrEmpty(filter.CameraId)) cmd.Parameters.AddWithValue("@camId", filter.CameraId);

        cmd.Parameters.AddWithValue("@limit", Math.Max(1, filter.PageSize));
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, filter.PageIndex * filter.PageSize));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var eventId = Guid.Parse(reader.GetString(0));
            results.Add(new FaceEventRecord
            {
                EventId = eventId,
                CameraId = reader.GetString(1),
                ChannelIndex = reader.GetInt32(2),
                TimestampUtc = DateTime.Parse(reader.GetString(3)),
                FaceCount = reader.GetInt32(4),
                CreatedAtUtc = DateTime.Parse(reader.GetString(5))
            });
        }

        // واکشی برش‌های مرتبط
        foreach (var ev in results)
        {
            await using var cropCmd = conn.CreateCommand();
            cropCmd.CommandText = @"
                SELECT CropId, CropIndex, Confidence, NormX, NormY, NormW, NormH, EncryptedFilePath, IsPendingDeletion
                FROM FaceCrops
                WHERE EventId = @evId;
            ";
            cropCmd.Parameters.AddWithValue("@evId", ev.EventId.ToString("N"));
            await using var cropReader = await cropCmd.ExecuteReaderAsync();
            while (await cropReader.ReadAsync())
            {
                ev.Crops.Add(new FaceCropRecord
                {
                    CropId = Guid.Parse(cropReader.GetString(0)),
                    EventId = ev.EventId,
                    CropIndex = (ushort)cropReader.GetInt32(1),
                    Confidence = cropReader.GetFloat(2),
                    NormX = cropReader.GetFloat(3),
                    NormY = cropReader.GetFloat(4),
                    NormW = cropReader.GetFloat(5),
                    NormH = cropReader.GetFloat(6),
                    EncryptedFilePath = cropReader.GetString(7),
                    IsPendingDeletion = cropReader.GetInt32(8) == 1
                });
            }
        }

        return results;
    }

    public async Task<bool> MarkCropPendingDeletionAsync(Guid cropId)
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE FaceCrops SET IsPendingDeletion = 1 WHERE CropId = @cropId;";
            cmd.Parameters.AddWithValue("@cropId", cropId.ToString("N"));
            int rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> CompleteCropDeletionAsync(Guid cropId, Guid parentEventId)
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // ۱. حذف رکورد فرزند
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = "DELETE FROM FaceCrops WHERE CropId = @cropId;";
                cmd.Parameters.AddWithValue("@cropId", cropId.ToString("N"));
                await cmd.ExecuteNonQueryAsync();
            }

            // ۲. بررسی وجود فرزندان دیگر برای والد
            bool hasOtherChildren;
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = (SqliteTransaction)tx;
                checkCmd.CommandText = "SELECT COUNT(*) FROM FaceCrops WHERE EventId = @evId;";
                checkCmd.Parameters.AddWithValue("@evId", parentEventId.ToString("N"));
                long count = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);
                hasOtherChildren = count > 0;
            }

            // ۳. حذف والد فقط در صورت عدم وجود هرگونه فرزند دیگر
            if (!hasOtherChildren)
            {
                await using var delEventCmd = conn.CreateCommand();
                delEventCmd.Transaction = (SqliteTransaction)tx;
                delEventCmd.CommandText = "DELETE FROM FaceEvents WHERE EventId = @evId;";
                delEventCmd.Parameters.AddWithValue("@evId", parentEventId.ToString("N"));
                await delEventCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<List<FaceCropRecord>> GetPendingDeletionsAsync()
    {
        var results = new List<FaceCropRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CropId, EventId, EncryptedFilePath FROM FaceCrops WHERE IsPendingDeletion = 1;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new FaceCropRecord
            {
                CropId = Guid.Parse(reader.GetString(0)),
                EventId = Guid.Parse(reader.GetString(1)),
                EncryptedFilePath = reader.GetString(2),
                IsPendingDeletion = true
            });
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _writeGate.Dispose();
        }
        await Task.CompletedTask;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
