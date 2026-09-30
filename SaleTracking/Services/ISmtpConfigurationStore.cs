using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace SaleTracking.Services;

public interface ISmtpConfigurationStore
{
    SmtpOptions GetOptions();
    void SaveOptions(SmtpOptions options);
}

public sealed class SqliteSmtpConfigurationStore(
    SqliteDatabase database,
    IOptions<SmtpOptions> fallbackOptions) : ISmtpConfigurationStore
{
    private static readonly string[] Keys =
    [
        "smtp:host",
        "smtp:port",
        "smtp:enable_ssl",
        "smtp:sender_email",
        "smtp:sender_name",
        "smtp:password"
    ];

    public SmtpOptions GetOptions()
    {
        var fallback = fallbackOptions.Value ?? new SmtpOptions();
        var options = new SmtpOptions
        {
            Host = fallback.Host,
            Port = fallback.Port,
            EnableSsl = fallback.EnableSsl,
            SenderEmail = fallback.SenderEmail,
            SenderName = fallback.SenderName,
            Username = fallback.Username,
            Password = fallback.Password
        };

        try
        {
            using var connection = database.OpenConnection();
            using var command = SqliteCommands.Create(connection,
                "SELECT Key, Value FROM ApplicationMetadata WHERE Key LIKE 'smtp:%';");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(value)) continue;

                switch (key)
                {
                    case "smtp:host":
                        options.Host = value.Trim();
                        break;
                    case "smtp:port":
                        if (int.TryParse(value, out var port) && port > 0) options.Port = port;
                        break;
                    case "smtp:enable_ssl":
                        if (bool.TryParse(value, out var ssl)) options.EnableSsl = ssl;
                        break;
                    case "smtp:sender_email":
                        options.SenderEmail = value.Trim();
                        break;
                    case "smtp:sender_name":
                        options.SenderName = value.Trim();
                        break;
                    case "smtp:password":
                        options.Password = value.Trim();
                        break;
                }
            }
        }
        catch
        {
            // If the database is initializing or unavailable, use the fallback options
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            options.Username = options.SenderEmail;
        }

        return options;
    }

    public void SaveOptions(SmtpOptions options)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        SaveKey(connection, transaction, "smtp:host", options.Host?.Trim() ?? "");
        SaveKey(connection, transaction, "smtp:port", options.Port.ToString());
        SaveKey(connection, transaction, "smtp:enable_ssl", options.EnableSsl.ToString());
        SaveKey(connection, transaction, "smtp:sender_email", options.SenderEmail?.Trim() ?? "");
        SaveKey(connection, transaction, "smtp:sender_name", options.SenderName?.Trim() ?? "");

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            SaveKey(connection, transaction, "smtp:password", options.Password.Trim());
        }

        transaction.Commit();
    }

    private static void SaveKey(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, string key, string value)
    {
        using var cmd = SqliteCommands.Create(connection,
            "INSERT OR REPLACE INTO ApplicationMetadata (Key, Value) VALUES ($key, $val);",
            transaction, ("$key", key), ("$val", value));
        cmd.ExecuteNonQuery();
    }
}
