using Microsoft.Data.Sqlite;
using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore, IAdminAccountStore
{
    public bool IsConfigured
    {
        get
        {
            using var connection = database.OpenConnection();
            using var command = SqliteCommands.Create(connection, "SELECT EXISTS(SELECT 1 FROM Users WHERE IsAdmin=1);");
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        }
    }

    public bool Add(ApplicationUser user) => !user.IsAdmin && TryInsert(user);
    public bool TryCreate(ApplicationUser user) => user is { IsAdmin: true, IsWhatsAppVerified: true } && TryInsert(user);

    private bool TryInsert(ApplicationUser user)
    {
        using var connection = database.OpenConnection();
        try { InsertUser(connection, user); return true; }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode is 1555 or 2067) { return false; }
    }

    internal static void InsertUser(SqliteConnection connection, ApplicationUser user, SqliteTransaction? transaction = null)
    {
        using var command = SqliteCommands.Create(connection, """
            INSERT INTO Users (Id, Name, Email, WhatsAppNumber, PasswordHash, IsWhatsAppVerified, IsAdmin,
                VerificationCode, CodeExpiresAt, DefaultCheckIntervalMinutes, DefaultNotificationPreference)
            VALUES ($id, $name, $email, $phone, $hash, $verified, $admin, $code, $expires, $interval, $preference);
            """, transaction,
            ("$id", user.Id.ToString()), ("$name", user.Name.Trim()), ("$email", NormalizeEmail(user.Email)),
            ("$phone", user.WhatsAppNumber.Trim()), ("$hash", user.PasswordHash), ("$verified", user.IsWhatsAppVerified),
            ("$admin", user.IsAdmin), ("$code", user.VerificationCode), ("$expires", user.CodeExpiresAt?.UtcTicks),
            ("$interval", user.DefaultCheckIntervalMinutes), ("$preference", user.DefaultNotificationPreference));
        command.ExecuteNonQuery();
    }

    public ApplicationUser? FindByEmail(string email)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            SELECT Id, Name, Email, WhatsAppNumber, PasswordHash, IsWhatsAppVerified, IsAdmin,
                VerificationCode, CodeExpiresAt, DefaultCheckIntervalMinutes, DefaultNotificationPreference
            FROM Users WHERE Email=$email;
            """, null, ("$email", NormalizeEmail(email)));
        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new ApplicationUser
        {
            Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), Email = reader.GetString(2),
            WhatsAppNumber = reader.GetString(3), PasswordHash = reader.GetString(4), IsWhatsAppVerified = reader.GetBoolean(5),
            IsAdmin = reader.GetBoolean(6), VerificationCode = SqliteCommands.Text(reader, 7),
            CodeExpiresAt = SqliteCommands.Instant(reader, 8), DefaultCheckIntervalMinutes = reader.GetInt32(9),
            DefaultNotificationPreference = reader.GetString(10)
        };
    }

    public bool TryVerifyWhatsApp(string email, string code, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            UPDATE Users SET IsWhatsAppVerified=1, VerificationCode=NULL, CodeExpiresAt=NULL
            WHERE Email=$email AND IsWhatsAppVerified=0 AND VerificationCode=$code AND CodeExpiresAt >= $now;
            """, null, ("$email", NormalizeEmail(email)), ("$code", code), ("$now", now.UtcTicks));
        return command.ExecuteNonQuery() == 1;
    }

    public bool UpdateProfile(string currentEmail, string newName, string newEmail, string newWhatsAppNumber,
        int defaultCheckIntervalMinutes, string defaultNotificationPreference)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, """
            UPDATE Users SET Name=$name, Email=$email, WhatsAppNumber=$phone,
                DefaultCheckIntervalMinutes=$interval, DefaultNotificationPreference=$preference WHERE Email=$current;
            """, null, ("$name", newName.Trim()), ("$email", NormalizeEmail(newEmail)), ("$phone", newWhatsAppNumber.Trim()),
            ("$interval", defaultCheckIntervalMinutes), ("$preference", defaultNotificationPreference),
            ("$current", NormalizeEmail(currentEmail)));
        try { return command.ExecuteNonQuery() == 1; }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067) { return false; }
    }

    public bool UpdatePassword(string email, string newPasswordHash)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteCommands.Create(connection, "UPDATE Users SET PasswordHash=$hash WHERE Email=$email;",
            null, ("$hash", newPasswordHash), ("$email", NormalizeEmail(email)));
        return command.ExecuteNonQuery() == 1;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
