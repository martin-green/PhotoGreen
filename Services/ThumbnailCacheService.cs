using System.IO;
using Microsoft.Data.Sqlite;

namespace PhotoGreen.Services;

/// <summary>
/// SQLite-backed cache for pre-scaled JPEG thumbnail bytes.
/// Supports sub-libraries: if a subfolder contains its own
/// <c>.photogreen-thumbs.db</c>, images in that subtree are cached there
/// with paths relative to the sub-library root.  The root folder always
/// has a database that serves as the fallback for unclaimed paths.
/// </summary>
public sealed class ThumbnailCacheService : IDisposable
{
    private const string DbFileName = ".photogreen-thumbs.db";

    /// <summary>
    /// Sorted deepest-first so <see cref="Resolve"/> finds the most
    /// specific sub-library before falling back to the root.
    /// </summary>
    private readonly List<CacheNode> _nodes;

    private volatile bool _disposed;

    public ThumbnailCacheService(string rootFolder)
    {
        _nodes = DiscoverCacheNodes(rootFolder);
    }

    /// <summary>
    /// Returns cached JPEG thumbnail bytes, or <c>null</c> if the entry is missing or stale.
    /// </summary>
    public byte[]? Get(string relativePath, long fileSize, DateTime lastModifiedUtc)
    {
        if (_disposed)
            return null;

        var (node, adjustedPath) = Resolve(relativePath);

        // The whole command lifetime (create/execute/dispose) must be inside the
        // lock: SqliteConnection is not thread-safe and creating or disposing a
        // command mutates connection state shared with Close()/Dispose().
        lock (node.SyncLock)
        {
            if (_disposed)
                return null;

            using var cmd = node.Connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT JpegData FROM Thumbnails
                WHERE RelativePath = @path
                  AND FileSize = @size
                  AND LastModifiedUtc = @modified
                """;
            cmd.Parameters.AddWithValue("@path", adjustedPath);
            cmd.Parameters.AddWithValue("@size", fileSize);
            cmd.Parameters.AddWithValue("@modified", lastModifiedUtc.ToString("O"));

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
        if (_disposed)
            return;

        var (node, adjustedPath) = Resolve(relativePath);

        lock (node.SyncLock)
        {
            if (_disposed)
                return;

            using var cmd = node.Connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO Thumbnails (RelativePath, FileSize, LastModifiedUtc, JpegData)
                VALUES (@path, @size, @modified, @data)
                """;
            cmd.Parameters.AddWithValue("@path", adjustedPath);
            cmd.Parameters.AddWithValue("@size", fileSize);
            cmd.Parameters.AddWithValue("@modified", lastModifiedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@data", jpegData);

            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Take each node's lock so we never close a connection while a
        // background thumbnail worker is executing against it.
        foreach (var node in _nodes)
        {
            lock (node.SyncLock)
            {
                node.Connection.Dispose();
            }
        }
    }

    // ── private helpers ─────────────────────────────────────────────

    /// <summary>
    /// Finds the deepest <c>.photogreen-thumbs.db</c> whose directory is an
    /// ancestor of (or equal to) the image path and returns the adjusted
    /// relative path for storage in that database.
    /// </summary>
    private (CacheNode Node, string AdjustedPath) Resolve(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');

        foreach (var node in _nodes)
        {
            // Root node (empty prefix) is always last and matches everything
            if (node.Prefix.Length == 0)
                return (node, relativePath);

            if (normalized.StartsWith(node.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                // Strip the sub-library directory prefix from the key
                var adjusted = relativePath.Substring(node.Prefix.Length);
                return (node, adjusted);
            }
        }

        // Should never happen — root is always present as final fallback
        return (_nodes[^1], relativePath);
    }

    private static List<CacheNode> DiscoverCacheNodes(string rootFolder)
    {
        var nodes = new List<CacheNode>();

        // Always ensure the root has a database
        nodes.Add(CreateNode(rootFolder, string.Empty));

        // Discover existing databases in subfolders (sub-libraries)
        try
        {
            foreach (var dbFile in Directory.EnumerateFiles(
                         rootFolder, DbFileName, SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(dbFile)!;
                var relDir = Path.GetRelativePath(rootFolder, dir);
                if (relDir == ".")
                    continue; // Already added as root

                nodes.Add(CreateNode(dir, relDir));
            }
        }
        catch
        {
            // Ignore discovery errors (permissions, etc.)
        }

        // Sort deepest-first so Resolve matches the most specific sub-library
        nodes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));

        return nodes;
    }

    private static CacheNode CreateNode(string absoluteDir, string relativeDir)
    {
        var dbPath = Path.Combine(absoluteDir, DbFileName);
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
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

        // Pre-compute the forward-slash prefix with trailing separator
        // so Resolve can do a single StartsWith check.
        var prefix = relativeDir.Length > 0
            ? relativeDir.Replace('\\', '/') + "/"
            : string.Empty;

        return new CacheNode(prefix, connection);
    }

    private sealed class CacheNode(string prefix, SqliteConnection connection)
    {
        public string Prefix { get; } = prefix;
        public SqliteConnection Connection { get; } = connection;
        public object SyncLock { get; } = new();
    }
}
