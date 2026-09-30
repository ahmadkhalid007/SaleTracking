using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace SaleTracking.Tests;

public class DumpDbTest(ITestOutputHelper output)
{
    [Fact]
    public void DumpDatabaseState()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, ".local", "saletracking.db")))
        {
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        var dbPath = Path.Combine(dir, ".local", "saletracking.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            ForeignKeys = true
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        output.WriteLine("=== USERS ===");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Email, Name, WhatsAppNumber, DefaultNotificationPreference, IsAdmin FROM Users;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                output.WriteLine($"User: {reader["Email"]} | Name: {reader["Name"]} | Phone: {reader["WhatsAppNumber"]} | Pref: {reader["DefaultNotificationPreference"]} | Admin: {reader["IsAdmin"]}");
            }
        }

        output.WriteLine("=== TRACKS ===");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TrackingId, OwnerId, TargetPrice, CurrentPrice, IsNotified, NotificationMessageSid, LastAlertError, TargetWhatsAppNumber FROM TrackingItems ORDER BY CreatedAt DESC LIMIT 10;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                output.WriteLine($"Track: {reader["TrackingId"]} | Target: {reader["TargetPrice"]} | LastPrice: {reader["CurrentPrice"]} | Notified: {reader["IsNotified"]} | Sids: {reader["NotificationMessageSid"]} | AlertErr: {reader["LastAlertError"]} | Phone: {reader["TargetWhatsAppNumber"]}");
            }
        }
    }
}
