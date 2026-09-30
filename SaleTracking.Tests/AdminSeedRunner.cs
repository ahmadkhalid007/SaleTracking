using System.Text.Json;
using Microsoft.Data.Sqlite;
using SaleTracking.Models;
using SaleTracking.Services;
using Xunit;

namespace SaleTracking.Tests;

public class AdminSeedRunner
{
    [Fact]
    public void SeedAdminAccount()
    {
        const string adminEmail = "criperfect711@gmail.com";
        const string adminPassword = "123456789@A";
        const string adminName = "Administrator";

        var newPasswordHash = PasswordSecurity.Hash(adminPassword);
        Assert.True(PasswordSecurity.Verify(adminPassword, newPasswordHash));

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, ".local", "saletracking.db")))
        {
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        var dbPath = Path.Combine(dir, ".local", "saletracking.db");
        Assert.True(File.Exists(dbPath), $"Database file not found at {dbPath}");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();

            // 1. Check if user with adminEmail exists
            string? existingUserId = null;
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT Id FROM Users WHERE Email = $email COLLATE NOCASE;";
                checkCmd.Parameters.AddWithValue("$email", adminEmail);
                var result = checkCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    existingUserId = result.ToString();
                }
            }

            // 2. Check if another admin exists
            string? otherAdminId = null;
            using (var adminCmd = connection.CreateCommand())
            {
                adminCmd.CommandText = "SELECT Id FROM Users WHERE IsAdmin = 1;";
                var result = adminCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    otherAdminId = result.ToString();
                }
            }

            using var transaction = connection.BeginTransaction();

            if (otherAdminId != null && otherAdminId != existingUserId)
            {
                // Demote or remove previous admin flag to satisfy single admin unique constraint
                using var demoteCmd = connection.CreateCommand();
                demoteCmd.Transaction = transaction;
                demoteCmd.CommandText = "UPDATE Users SET IsAdmin = 0 WHERE Id = $id;";
                demoteCmd.Parameters.AddWithValue("$id", otherAdminId);
                demoteCmd.ExecuteNonQuery();
            }

            if (existingUserId != null)
            {
                // Update existing user
                using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = """
                    UPDATE Users
                    SET Name = $name,
                        PasswordHash = $hash,
                        IsAdmin = 1,
                        IsWhatsAppVerified = 1,
                        DefaultNotificationPreference = 'Both'
                    WHERE Id = $id;
                    """;
                updateCmd.Parameters.AddWithValue("$name", adminName);
                updateCmd.Parameters.AddWithValue("$hash", newPasswordHash);
                updateCmd.Parameters.AddWithValue("$id", existingUserId);
                updateCmd.ExecuteNonQuery();
            }
            else
            {
                // Insert new admin user
                var newId = Guid.NewGuid().ToString();
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = """
                    INSERT INTO Users (
                        Id, Name, Email, WhatsAppNumber, PasswordHash,
                        IsWhatsAppVerified, IsAdmin, DefaultCheckIntervalMinutes, DefaultNotificationPreference
                    ) VALUES (
                        $id, $name, $email, $phone, $hash,
                        1, 1, 1, 'Both'
                    );
                    """;
                insertCmd.Parameters.AddWithValue("$id", newId);
                insertCmd.Parameters.AddWithValue("$name", adminName);
                insertCmd.Parameters.AddWithValue("$email", adminEmail.ToLowerInvariant());
                insertCmd.Parameters.AddWithValue("$phone", "+923014488838");
                insertCmd.Parameters.AddWithValue("$hash", newPasswordHash);
                insertCmd.ExecuteNonQuery();
            }

            transaction.Commit();

            // Verify in database
            using var verifyCmd = connection.CreateCommand();
            verifyCmd.CommandText = "SELECT Name, Email, PasswordHash, IsAdmin, IsWhatsAppVerified FROM Users WHERE Email = $email COLLATE NOCASE;";
            verifyCmd.Parameters.AddWithValue("$email", adminEmail);
            using var reader = verifyCmd.ExecuteReader();
            Assert.True(reader.Read(), "Admin user was not found after seeding.");
            Assert.Equal(adminEmail, reader.GetString(1), ignoreCase: true);
            var storedHash = reader.GetString(2);
            Assert.True(reader.GetBoolean(3), "User is not admin.");
            Assert.True(reader.GetBoolean(4), "User WhatsApp is not verified.");
            Assert.True(PasswordSecurity.Verify(adminPassword, storedHash), "Password hash does not verify with seeded password.");
        }

        // Also update .local/admin/account.json for backup/legacy compatibility
        var accountJsonPath = Path.Combine(dir, ".local", "admin", "account.json");

        if (File.Exists(accountJsonPath))
        {
            var adminUser = new ApplicationUser
            {
                Name = adminName,
                Email = adminEmail,
                WhatsAppNumber = "+923014488838",
                PasswordHash = newPasswordHash,
                IsWhatsAppVerified = true,
                IsAdmin = true,
                DefaultCheckIntervalMinutes = 1,
                DefaultNotificationPreference = "Both"
            };
            File.WriteAllText(accountJsonPath, JsonSerializer.Serialize(adminUser));
        }
    }
}
