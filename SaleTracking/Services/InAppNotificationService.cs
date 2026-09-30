using System.Globalization;
using Microsoft.Data.Sqlite;
using SaleTracking.Models;

namespace SaleTracking.Services;

public interface IInAppNotificationService
{
    Task<long> CreateNotificationAsync(
        string userEmail,
        string trackingId,
        string? productName,
        string productUrl,
        decimal currentPrice,
        decimal targetPrice,
        string message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InAppNotification>> GetNotificationsAsync(
        string userEmail,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(
        string userEmail,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAsReadAsync(
        long notificationId,
        string userEmail,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAllAsReadAsync(
        string userEmail,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteNotificationAsync(
        long notificationId,
        string userEmail,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteInAppNotificationService : IInAppNotificationService
{
    private readonly SqliteDatabase database;
    private readonly object initLock = new();
    private bool initialized;

    public SqliteInAppNotificationService(SqliteDatabase database)
    {
        this.database = database;
        EnsureTableCreated();
    }

    private void EnsureTableCreated()
    {
        if (initialized) return;
        lock (initLock)
        {
            if (initialized) return;
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS InAppNotifications (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserEmail TEXT NOT NULL,
                    TrackingId TEXT NOT NULL,
                    ProductName TEXT,
                    ProductUrl TEXT NOT NULL,
                    CurrentPrice REAL NOT NULL,
                    TargetPrice REAL NOT NULL,
                    Message TEXT NOT NULL,
                    IsRead INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_InAppNotifications_UserEmail ON InAppNotifications(UserEmail, CreatedAt DESC);
            """;
            cmd.ExecuteNonQuery();
            initialized = true;
        }
    }

    public async Task<long> CreateNotificationAsync(
        string userEmail,
        string trackingId,
        string? productName,
        string productUrl,
        decimal currentPrice,
        decimal targetPrice,
        string message,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO InAppNotifications (
                UserEmail, TrackingId, ProductName, ProductUrl, CurrentPrice, TargetPrice, Message, IsRead, CreatedAt
            ) VALUES (
                $email, $trackingId, $productName, $url, $currentPrice, $targetPrice, $message, 0, $createdAt
            );
            SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$email", userEmail.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$trackingId", trackingId);
        cmd.Parameters.AddWithValue("$productName", (object?)productName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", productUrl);
        cmd.Parameters.AddWithValue("$currentPrice", Convert.ToDouble(currentPrice));
        cmd.Parameters.AddWithValue("$targetPrice", Convert.ToDouble(targetPrice));
        cmd.Parameters.AddWithValue("$message", message);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<InAppNotification>> GetNotificationsAsync(
        string userEmail,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, UserEmail, TrackingId, ProductName, ProductUrl, CurrentPrice, TargetPrice, Message, IsRead, CreatedAt
            FROM InAppNotifications
            WHERE UserEmail = $email
            ORDER BY Id DESC
            LIMIT $limit;
        """;
        cmd.Parameters.AddWithValue("$email", userEmail.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<InAppNotification>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var notif = new InAppNotification
            {
                Id = reader.GetInt64(0),
                UserEmail = reader.GetString(1),
                TrackingId = reader.GetString(2),
                ProductName = reader.IsDBNull(3) ? null : reader.GetString(3),
                ProductUrl = reader.GetString(4),
                CurrentPrice = Convert.ToDecimal(reader.GetDouble(5)),
                TargetPrice = Convert.ToDecimal(reader.GetDouble(6)),
                Message = reader.GetString(7),
                IsRead = reader.GetInt32(8) == 1,
                CreatedAt = DateTime.TryParse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                    ? dt : DateTime.UtcNow
            };
            list.Add(notif);
        }
        return list;
    }

    public async Task<int> GetUnreadCountAsync(
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM InAppNotifications WHERE UserEmail = $email AND IsRead = 0;";
        cmd.Parameters.AddWithValue("$email", userEmail.Trim().ToLowerInvariant());

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<bool> MarkAsReadAsync(
        long notificationId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE InAppNotifications SET IsRead = 1 WHERE Id = $id AND UserEmail = $email;";
        cmd.Parameters.AddWithValue("$id", notificationId);
        cmd.Parameters.AddWithValue("$email", userEmail.Trim().ToLowerInvariant());

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<bool> MarkAllAsReadAsync(
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE InAppNotifications SET IsRead = 1 WHERE UserEmail = $email AND IsRead = 0;";
        cmd.Parameters.AddWithValue("$email", userEmail.Trim().ToLowerInvariant());

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<bool> DeleteNotificationAsync(
        long notificationId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM InAppNotifications WHERE Id = $id AND UserEmail = $email;";
        cmd.Parameters.AddWithValue("$id", notificationId);
        cmd.Parameters.AddWithValue("$email", userEmail.Trim().ToLowerInvariant());

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }
}

public sealed class InMemoryInAppNotificationService : IInAppNotificationService
{
    private readonly List<InAppNotification> notifications = [];
    private long nextId = 1;
    private readonly object gate = new();

    public Task<long> CreateNotificationAsync(
        string userEmail,
        string trackingId,
        string? productName,
        string productUrl,
        decimal currentPrice,
        decimal targetPrice,
        string message,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var notif = new InAppNotification
            {
                Id = nextId++,
                UserEmail = userEmail.Trim().ToLowerInvariant(),
                TrackingId = trackingId,
                ProductName = productName,
                ProductUrl = productUrl,
                CurrentPrice = currentPrice,
                TargetPrice = targetPrice,
                Message = message,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };
            notifications.Add(notif);
            return Task.FromResult(notif.Id);
        }
    }

    public Task<IReadOnlyList<InAppNotification>> GetNotificationsAsync(
        string userEmail,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var normalizedEmail = userEmail.Trim().ToLowerInvariant();
            var result = notifications
                .Where(n => n.UserEmail == normalizedEmail)
                .OrderByDescending(n => n.Id)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<InAppNotification>>(result);
        }
    }

    public Task<int> GetUnreadCountAsync(
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var normalizedEmail = userEmail.Trim().ToLowerInvariant();
            var count = notifications.Count(n => n.UserEmail == normalizedEmail && !n.IsRead);
            return Task.FromResult(count);
        }
    }

    public Task<bool> MarkAsReadAsync(
        long notificationId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var normalizedEmail = userEmail.Trim().ToLowerInvariant();
            var notif = notifications.FirstOrDefault(n => n.Id == notificationId && n.UserEmail == normalizedEmail);
            if (notif is not null)
            {
                notif.IsRead = true;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    public Task<bool> MarkAllAsReadAsync(
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var normalizedEmail = userEmail.Trim().ToLowerInvariant();
            var matched = notifications.Where(n => n.UserEmail == normalizedEmail && !n.IsRead).ToList();
            foreach (var n in matched) n.IsRead = true;
            return Task.FromResult(matched.Count > 0);
        }
    }

    public Task<bool> DeleteNotificationAsync(
        long notificationId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var normalizedEmail = userEmail.Trim().ToLowerInvariant();
            var index = notifications.FindIndex(n => n.Id == notificationId && n.UserEmail == normalizedEmail);
            if (index >= 0)
            {
                notifications.RemoveAt(index);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }
}
