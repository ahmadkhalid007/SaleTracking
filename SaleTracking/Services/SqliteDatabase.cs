using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class SqliteDatabase
{
    private readonly string connectionString;
    public string FilePath { get; }

    public SqliteDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath, ForeignKeys = true, DefaultTimeout = 30,
            Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    public void Initialize(string? legacyAdminFile = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var connection = OpenConnection();
        using (var wal = SqliteCommands.Create(connection, "PRAGMA journal_mode=WAL;")) wal.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        using var versionQuery = SqliteCommands.Create(connection, "PRAGMA user_version;", transaction);
        var version = Convert.ToInt32(versionQuery.ExecuteScalar());
        if (version > 1) throw new InvalidOperationException("This database was created by a newer SaleTracking version. Upgrade the app before opening it.");
        if (version == 0)
        {
            using var stream = typeof(SqliteDatabase).Assembly.GetManifestResourceStream("SaleTracking.Database.Migrations.001_Initial.sql")
                ?? throw new InvalidOperationException("The initial database migration is missing.");
            using var reader = new StreamReader(stream);
            using var migration = SqliteCommands.Create(connection, reader.ReadToEnd(), transaction);
            migration.ExecuteNonQuery();
            using var setVersion = SqliteCommands.Create(connection, "PRAGMA user_version=1;", transaction);
            setVersion.ExecuteNonQuery();
        }
        EnsureTrackingColumns(connection, transaction);
        using (var receipts = SqliteCommands.Create(connection, """
            CREATE TABLE IF NOT EXISTS TrackingNotificationReceipts (
                TrackingId TEXT NOT NULL REFERENCES TrackingItems(TrackingId) ON DELETE CASCADE,
                Channel TEXT NOT NULL,
                MessageId TEXT NOT NULL,
                PRIMARY KEY (TrackingId, Channel)
            );
            """, transaction)) receipts.ExecuteNonQuery();
        ImportLegacyAdmin(connection, transaction, legacyAdminFile);
        transaction.Commit();
    }

    private static void EnsureTrackingColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = SqliteCommands.Create(connection, "PRAGMA table_info(TrackingItems);", transaction))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (existingColumns.Count > 0)
        {
            if (!existingColumns.Contains("CheckType"))
            {
                using var alter = SqliteCommands.Create(connection, "ALTER TABLE TrackingItems ADD COLUMN CheckType TEXT NOT NULL DEFAULT 'TargetPrice';", transaction);
                alter.ExecuteNonQuery();
            }
            if (!existingColumns.Contains("IsInStock"))
            {
                using var alter = SqliteCommands.Create(connection, "ALTER TABLE TrackingItems ADD COLUMN IsInStock INTEGER;", transaction);
                alter.ExecuteNonQuery();
            }
            if (!existingColumns.Contains("StartTime"))
            {
                using var alter = SqliteCommands.Create(connection, "ALTER TABLE TrackingItems ADD COLUMN StartTime TEXT;", transaction);
                alter.ExecuteNonQuery();
            }
            if (!existingColumns.Contains("EndTime"))
            {
                using var alter = SqliteCommands.Create(connection, "ALTER TABLE TrackingItems ADD COLUMN EndTime TEXT;", transaction);
                alter.ExecuteNonQuery();
            }
        }
    }

    private static void ImportLegacyAdmin(SqliteConnection connection, SqliteTransaction transaction, string? path)
    {
        using var marker = SqliteCommands.Create(connection,
            "SELECT COUNT(*) FROM ApplicationMetadata WHERE Key='legacy-admin-import';", transaction);
        if (Convert.ToInt64(marker.ExecuteScalar()) != 0) return;
        using var count = SqliteCommands.Create(connection, "SELECT COUNT(*) FROM Users WHERE IsAdmin=1;", transaction);
        var result = "not-found";
        if (Convert.ToInt64(count.ExecuteScalar()) != 0) result = "existing-admin-kept";
        else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            ApplicationUser? admin;
            try { admin = JsonSerializer.Deserialize<ApplicationUser>(File.ReadAllText(path)); }
            catch (JsonException ex) { throw new InvalidDataException("The saved administrator file is damaged. Restore its backup before migrating.", ex); }
            if (admin is not { IsAdmin: true, IsWhatsAppVerified: true } || admin.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.PasswordHash))
                throw new InvalidDataException("The saved administrator file is invalid. Restore its backup before migrating.");
            SqliteAccountStore.InsertUser(connection, admin, transaction);
            result = "imported";
        }
        using var save = SqliteCommands.Create(connection,
            "INSERT INTO ApplicationMetadata (Key, Value) VALUES ('legacy-admin-import', $result);", transaction, ("$result", result));
        save.ExecuteNonQuery();
    }
}

internal static class SqliteCommands
{
    public static SqliteCommand Create(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string name, object? value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? Time(TimeOnly? value) => value?.ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string? Money(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
    public static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    public static DateTimeOffset? Instant(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : new(reader.GetInt64(index), TimeSpan.Zero);
    public static DateOnly? Date(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateOnly.ParseExact(reader.GetString(index), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static TimeOnly? Time(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : TimeOnly.ParseExact(reader.GetString(index), "HH:mm", CultureInfo.InvariantCulture);
    public static decimal? Money(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : decimal.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
}
