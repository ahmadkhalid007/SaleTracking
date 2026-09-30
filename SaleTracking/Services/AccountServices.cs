using System.Collections.Concurrent; using System.Security.Cryptography; using SaleTracking.Models;
namespace SaleTracking.Services;
public interface IAccountStore
{
    bool Add(ApplicationUser user);
    ApplicationUser? FindByEmail(string email);
    bool TryVerifyWhatsApp(string email, string code, DateTimeOffset now);
    bool UpdateProfile(string currentEmail, string newName, string newEmail, string newWhatsAppNumber, int defaultCheckIntervalMinutes, string defaultNotificationPreference);
    bool UpdatePassword(string email, string newPasswordHash);
}

public interface IAdminAccountStore
{
    bool IsConfigured { get; }
    bool TryCreate(ApplicationUser user);
}

public class InMemoryAccountStore : IAccountStore
{
    private readonly ConcurrentDictionary<string, ApplicationUser> users = new(StringComparer.OrdinalIgnoreCase);
    private readonly object profileLock = new();

    public bool Add(ApplicationUser user)
    {
        lock (profileLock) return users.TryAdd(user.Email, user);
    }

    public ApplicationUser? FindByEmail(string email) => users.TryGetValue(email, out var u) ? u : null;

    public bool TryVerifyWhatsApp(string email, string code, DateTimeOffset now)
    {
        lock (profileLock)
        {
            if (!users.TryGetValue(email, out var user) || user.IsWhatsAppVerified || user.VerificationCode != code
                || !user.CodeExpiresAt.HasValue || user.CodeExpiresAt < now) return false;
            user.IsWhatsAppVerified = true;
            user.VerificationCode = null;
            user.CodeExpiresAt = null;
            return true;
        }
    }

    public bool UpdateProfile(string currentEmail, string newName, string newEmail, string newWhatsAppNumber, int defaultCheckIntervalMinutes, string defaultNotificationPreference)
    {
        lock (profileLock)
        {
            if (!users.TryGetValue(currentEmail, out var user))
                return false;

            var normalizedNewEmail = newEmail.Trim().ToLowerInvariant();
            if (!string.Equals(currentEmail, normalizedNewEmail, StringComparison.OrdinalIgnoreCase))
            {
                if (users.ContainsKey(normalizedNewEmail)) return false;
                users.TryRemove(currentEmail, out _);
                user.Email = normalizedNewEmail;
                users.TryAdd(normalizedNewEmail, user);
            }
            user.Name = newName.Trim();
            user.WhatsAppNumber = newWhatsAppNumber.Trim();
            user.DefaultCheckIntervalMinutes = defaultCheckIntervalMinutes;
            user.DefaultNotificationPreference = defaultNotificationPreference;
            return true;
        }
    }

    public bool UpdatePassword(string email, string newPasswordHash)
    {
        if (users.TryGetValue(email, out var user))
        {
            user.PasswordHash = newPasswordHash;
            return true;
        }
        return false;
    }
}

public interface IWhatsAppVerificationService { Task SendCodeAsync(string phone, string code); }
public class DevelopmentWhatsAppVerificationService(ILogger<DevelopmentWhatsAppVerificationService> logger) : IWhatsAppVerificationService { public Task SendCodeAsync(string phone, string code) { logger.LogInformation("WhatsApp verification code for {Phone}: {Code}",phone,code); return Task.CompletedTask; } }
public static class PasswordSecurity { public static string Hash(string password) { var salt=RandomNumberGenerator.GetBytes(16); var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA512,32); return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}"; } public static bool Verify(string password,string stored) { var p=stored.Split(':'); if(p.Length!=2)return false; var actual=Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromBase64String(p[0]),210000,HashAlgorithmName.SHA512,32); return CryptographicOperations.FixedTimeEquals(actual,Convert.FromBase64String(p[1])); } }
