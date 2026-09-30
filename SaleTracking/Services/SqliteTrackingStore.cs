using Microsoft.Data.Sqlite;
using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class SqliteTrackingStore(SqliteDatabase database, TrackingSchedule? schedule = null) : ITrackingStore
{
    private const string SelectTracks = """
        SELECT t.TrackingId, u.Email, t.ProductUrl, t.TargetPrice, t.CheckIntervalMinutes,
            t.StartDate, t.EndDate, t.IsActive, t.IsNotified, t.DisableAfterNotification, t.CurrentPrice,
            t.LastCheckedAt, t.NotifiedAt, t.NotificationMessageSid, t.LastCheckError, t.LastAlertError,
            t.TargetWhatsAppNumber, t.CreatedAt, t.CheckType, t.IsInStock, t.StartTime, t.EndTime
        FROM TrackingItems t INNER JOIN Users u ON u.Id=t.OwnerId
        """;

    public TrackingItem Create(string ownerEmail, CreateTrackViewModel model)
    {
        var id = $"TRK-{Guid.NewGuid():N}".ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            INSERT INTO TrackingItems (TrackingId, OwnerId, ProductUrl, TargetPrice, CheckIntervalMinutes,
                StartDate, EndDate, DisableAfterNotification, TargetWhatsAppNumber, CreatedAt,
                CheckType, IsInStock, StartTime, EndTime)
            SELECT $id, Id, $url, $price, $interval, $start, $end, $disable, $phone, $created,
                $checkType, $inStock, $startTime, $endTime
            FROM Users WHERE Email=$owner;
            """, null, ("$id", id), ("$url", model.ProductUrl.Trim()), ("$price", SqliteCommands.Money(model.TargetPrice ?? 0m)),
            ("$interval", model.CheckIntervalMinutes), ("$start", SqliteCommands.Date(model.StartDate)),
            ("$end", SqliteCommands.Date(model.EndDate)), ("$disable", model.DisableAfterNotification),
            ("$phone", model.WhatsAppNumber?.Trim()), ("$created", now.UtcTicks), ("$owner", ownerEmail),
            ("$checkType", string.IsNullOrWhiteSpace(model.CheckType) ? "TargetPrice" : model.CheckType),
            ("$inStock", DBNull.Value),
            ("$startTime", SqliteCommands.Time(model.StartTime)),
            ("$endTime", SqliteCommands.Time(model.EndTime)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The track owner does not exist.");
        return GetById(id, ownerEmail)!;
    }

    public IReadOnlyCollection<TrackingItem> GetForUser(string ownerEmail) =>
        ReadTracks(" WHERE u.Email=$owner ORDER BY t.CreatedAt DESC;", ("$owner", ownerEmail));

    public TrackingItem? GetById(string trackingId, string ownerEmail) =>
        ReadTracks(" WHERE t.TrackingId=$id AND u.Email=$owner;", ("$id", trackingId), ("$owner", ownerEmail)).SingleOrDefault();

    public bool Update(string trackingId, string ownerEmail, EditTrackViewModel model)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            UPDATE TrackingItems SET ProductUrl=$url, TargetPrice=$price, CheckIntervalMinutes=$interval,
                StartDate=$start, EndDate=$end, DisableAfterNotification=$disable, IsActive=$active,
                CheckType=$checkType, StartTime=$startTime, EndTime=$endTime
            WHERE TrackingId=$id AND OwnerId IN (SELECT Id FROM Users WHERE Email=$owner);
            """, null, ("$url", model.ProductUrl.Trim()), ("$price", SqliteCommands.Money(model.TargetPrice ?? 0m)),
            ("$interval", model.CheckIntervalMinutes), ("$start", SqliteCommands.Date(model.StartDate)),
            ("$end", SqliteCommands.Date(model.EndDate)), ("$disable", model.DisableAfterNotification),
            ("$active", model.IsActive), ("$id", trackingId), ("$owner", ownerEmail),
            ("$checkType", string.IsNullOrWhiteSpace(model.CheckType) ? "TargetPrice" : model.CheckType),
            ("$startTime", SqliteCommands.Time(model.StartTime)),
            ("$endTime", SqliteCommands.Time(model.EndTime)));
        return command.ExecuteNonQuery() == 1;
    }

    public bool Delete(string trackingId, string ownerEmail) => ChangeOwned(
        "DELETE FROM TrackingItems WHERE TrackingId=$id AND OwnerId IN (SELECT Id FROM Users WHERE Email=$owner);", trackingId, ownerEmail);

    public bool Toggle(string trackingId, string ownerEmail) => ChangeOwned(
        "UPDATE TrackingItems SET IsActive=1-IsActive WHERE TrackingId=$id AND OwnerId IN (SELECT Id FROM Users WHERE Email=$owner);", trackingId, ownerEmail);

    private bool ChangeOwned(string sql, string id, string owner)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, sql, null, ("$id", id), ("$owner", owner));
        return command.ExecuteNonQuery() == 1;
    }

    // Track ownership uses a stable account ID. Changing the email needs no data migration.
    public void MigrateUserTracks(string oldEmail, string newEmail) { }

    public IReadOnlyCollection<TrackingItem> GetDueTracks(DateTimeOffset now)
    {
        var today = SqliteCommands.Date((schedule ?? TrackingSchedule.Utc).Today(now));
        using (var connection = database.OpenConnection())
        using (var expire = SqliteCommands.Create(connection,
            "UPDATE TrackingItems SET IsActive=0 WHERE IsActive=1 AND EndDate < $today;", null, ("$today", today)))
            expire.ExecuteNonQuery();
        return ReadTracks("""
             WHERE t.IsActive=1 AND (t.StartDate IS NULL OR t.StartDate <= $today)
                AND (t.EndDate IS NULL OR t.EndDate >= $today)
                AND COALESCE(t.LastCheckedAt,t.CreatedAt) + t.CheckIntervalMinutes * $minute <= $now
             ORDER BY COALESCE(t.LastCheckedAt,t.CreatedAt);
            """, ("$today", today), ("$minute", TimeSpan.TicksPerMinute), ("$now", now.UtcTicks));
    }

    public void RecordPriceCheck(string trackingId, decimal? currentPrice, DateTimeOffset checkedAt, string? error = null) =>
        RecordPriceCheck(trackingId, currentPrice, null, checkedAt, error);

    public void RecordPriceCheck(string trackingId, decimal? currentPrice, bool? isInStock, DateTimeOffset checkedAt, string? error = null)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            UPDATE TrackingItems SET CurrentPrice=$price, IsInStock=$stock, LastCheckedAt=$checked, LastCheckError=$error WHERE TrackingId=$id;
            """, null, ("$price", SqliteCommands.Money(currentPrice)),
            ("$stock", isInStock.HasValue ? (isInStock.Value ? 1 : 0) : DBNull.Value),
            ("$checked", checkedAt.UtcTicks),
            ("$error", error), ("$id", trackingId));
        command.ExecuteNonQuery();
    }

    public void MarkNotified(string trackingId, DateTimeOffset notifiedAt, string messageSid)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            UPDATE TrackingItems SET IsNotified=1, NotifiedAt=$notified, NotificationMessageSid=$message,
                LastAlertError=NULL, IsActive=CASE WHEN DisableAfterNotification=1 THEN 0 ELSE IsActive END
            WHERE TrackingId=$id AND IsNotified=0;
            """, null, ("$notified", notifiedAt.UtcTicks), ("$message", messageSid), ("$id", trackingId));
        command.ExecuteNonQuery();
    }

    public void RecordAlertFailure(string trackingId, string error)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection,
            "UPDATE TrackingItems SET LastAlertError=$error WHERE TrackingId=$id;", null, ("$error", error), ("$id", trackingId));
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, string> GetNotificationReceipts(string trackingId)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection,
            "SELECT Channel, MessageId FROM TrackingNotificationReceipts WHERE TrackingId=$id;", null, ("$id", trackingId));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    public void RecordNotificationReceipt(string trackingId, string channel, string messageId)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            INSERT INTO TrackingNotificationReceipts (TrackingId, Channel, MessageId)
            SELECT TrackingId, $channel, $message FROM TrackingItems WHERE TrackingId=$id
            ON CONFLICT(TrackingId, Channel) DO NOTHING;
            """, null, ("$id", trackingId), ("$channel", channel), ("$message", messageId));
        command.ExecuteNonQuery();
    }

    private IReadOnlyCollection<TrackingItem> ReadTracks(string where, params (string name, object? value)[] parameters)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, SelectTracks + where, null, parameters);
        using var reader = command.ExecuteReader();
        var tracks = new List<TrackingItem>();
        while (reader.Read()) tracks.Add(new TrackingItem
        {
            TrackingId = reader.GetString(0), OwnerEmail = reader.GetString(1), ProductUrl = reader.GetString(2),
            TargetPrice = SqliteCommands.Money(reader, 3)!.Value, CheckIntervalMinutes = reader.GetInt32(4),
            StartDate = SqliteCommands.Date(reader, 5), EndDate = SqliteCommands.Date(reader, 6),
            IsActive = reader.GetBoolean(7), IsNotified = reader.GetBoolean(8), DisableAfterNotification = reader.GetBoolean(9),
            CurrentPrice = SqliteCommands.Money(reader, 10), LastCheckedAt = SqliteCommands.Instant(reader, 11),
            NotifiedAt = SqliteCommands.Instant(reader, 12), NotificationMessageSid = SqliteCommands.Text(reader, 13),
            LastCheckError = SqliteCommands.Text(reader, 14), LastAlertError = SqliteCommands.Text(reader, 15),
            TargetWhatsAppNumber = SqliteCommands.Text(reader, 16), CreatedAt = SqliteCommands.Instant(reader, 17)!.Value,
            CheckType = SqliteCommands.Text(reader, 18) ?? "TargetPrice",
            IsInStock = reader.IsDBNull(19) ? null : reader.GetInt32(19) == 1,
            StartTime = SqliteCommands.Time(reader, 20),
            EndTime = SqliteCommands.Time(reader, 21)
        });
        return tracks;
    }
}
