using System.IO;
using Microsoft.Data.Sqlite;

namespace PhotoGreen.Services;

/// <summary>
/// SQLite-backed cache for pre-scaled JPEG thumbnail bytes.
/// The database file is stored alongside the library JSON in the root folder.
/// Cache entries are keyed by (relativePath, fileSize, lastModifiedUtc) so that
/// edits or replacements automatically invalidate stale thumbnails.
/// </summary>
public sealed class ThumbnailCacheService : IDisposable
{
    private const string DbFileName = ".photogreen-thumbs.db";

    private readonly SqliteConnection _connection;

    public ThumbnailCacheService(string rootFolder)
    {
        var dbPath = Path.Combine(rootFolder, DbFileName);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS Thumbnails (
                RelativePath    TEXT    NOT NULL,
                FileSize        INTEGER NOT NULL,
                LastModifiedUtc TEXT    NOT NULL,
                JpegData        BLOB    NOT NULL,
                PRIMARY KEY (RelativePath)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns cached JPEG thumbnail bytes, or <c>null</c> if the entry is missing or stale.
    /// Thread-safe: each call uses its own command on the shared WAL connection.
    /// </summary>
    public byte[]? Get(string relativePath, long fileSize, DateTime lastModifiedUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT JpegData FROM Thumbnails
            WHERE RelativePath = @path
              AND FileSize = @size
              AND LastModifiedUtc = @modified
            """;
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@modified", lastModifiedUtc.ToString("O"));

        lock (_connection)
        {
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return (byte[])reader["JpegData"];
        }

        return null;
    }

    /// <summary>
    /// Inserts or replaces a cached thumbnail entry.
    /// </summary>
    public void Put(string relativePath, long fileSize, DateTime lastModifiedUtc, byte[] jpegData)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO Thumbnails (RelativePath, FileSize, LastModifiedUtc, JpegData)
            VALUES (@path, @size, @modified, @data)
            """;
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@modified", lastModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@data", jpegData);

        lock (_connection)
        {
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
