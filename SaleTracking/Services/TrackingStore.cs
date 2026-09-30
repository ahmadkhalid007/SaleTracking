using System.Collections.Concurrent;
using SaleTracking.Models;

namespace SaleTracking.Services;

public interface ITrackingStore
{
    TrackingItem Create(string ownerEmail, CreateTrackViewModel model);
    IReadOnlyCollection<TrackingItem> GetForUser(string ownerEmail);
    TrackingItem? GetById(string trackingId, string ownerEmail);
    bool Update(string trackingId, string ownerEmail, EditTrackViewModel model);
    bool Delete(string trackingId, string ownerEmail);
    bool Toggle(string trackingId, string ownerEmail);
    void MigrateUserTracks(string oldEmail, string newEmail);
    IReadOnlyCollection<TrackingItem> GetDueTracks(DateTimeOffset now);
    void RecordPriceCheck(string trackingId, decimal? currentPrice, DateTimeOffset checkedAt, string? error = null);
    void RecordPriceCheck(string trackingId, decimal? currentPrice, bool? isInStock, DateTimeOffset checkedAt, string? error = null);
    void MarkNotified(string trackingId, DateTimeOffset notifiedAt, string messageSid);
    void RecordAlertFailure(string trackingId, string error);
    IReadOnlyDictionary<string, string> GetNotificationReceipts(string trackingId);
    void RecordNotificationReceipt(string trackingId, string channel, string messageId);
}

public sealed class InMemoryTrackingStore(TrackingSchedule? schedule = null) : ITrackingStore
{
    private readonly ConcurrentDictionary<string, TrackingItem> _tracks = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> receipts = new();

    public TrackingItem Create(string ownerEmail, CreateTrackViewModel model)
    {
        var trackingId = $"TRK-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var item = new TrackingItem
        {
            TrackingId = trackingId, OwnerEmail = ownerEmail, ProductUrl = model.ProductUrl.Trim(),
            TargetPrice = model.TargetPrice ?? 0m, CheckIntervalMinutes = model.CheckIntervalMinutes,
            StartDate = model.StartDate, EndDate = model.EndDate,
            StartTime = model.StartTime, EndTime = model.EndTime,
            CheckType = string.IsNullOrWhiteSpace(model.CheckType) ? "TargetPrice" : model.CheckType,
            DisableAfterNotification = model.DisableAfterNotification,
            TargetWhatsAppNumber = model.WhatsAppNumber?.Trim()
        };
        _tracks.TryAdd(trackingId, item);
        return item;
    }

    public IReadOnlyCollection<TrackingItem> GetForUser(string ownerEmail) => _tracks.Values
        .Where(item => string.Equals(item.OwnerEmail, ownerEmail, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => item.CreatedAt).ToArray();

    public TrackingItem? GetById(string trackingId, string ownerEmail)
    {
        if (_tracks.TryGetValue(trackingId, out var item) &&
            string.Equals(item.OwnerEmail, ownerEmail, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }
        return null;
    }

    public bool Update(string trackingId, string ownerEmail, EditTrackViewModel model)
    {
        if (!_tracks.TryGetValue(trackingId, out var item) ||
            !string.Equals(item.OwnerEmail, ownerEmail, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        item.ProductUrl = model.ProductUrl.Trim();
        item.TargetPrice = model.TargetPrice ?? 0m;
        item.CheckIntervalMinutes = model.CheckIntervalMinutes;
        item.StartDate = model.StartDate;
        item.EndDate = model.EndDate;
        item.StartTime = model.StartTime;
        item.EndTime = model.EndTime;
        item.CheckType = string.IsNullOrWhiteSpace(model.CheckType) ? "TargetPrice" : model.CheckType;
        item.DisableAfterNotification = model.DisableAfterNotification;
        item.IsActive = model.IsActive;
        return true;
    }

    public bool Delete(string trackingId, string ownerEmail)
    {
        if (!_tracks.TryGetValue(trackingId, out var item) ||
            !string.Equals(item.OwnerEmail, ownerEmail, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        receipts.TryRemove(trackingId, out _);
        return _tracks.TryRemove(trackingId, out _);
    }

    public void MigrateUserTracks(string oldEmail, string newEmail)
    {
        foreach (var track in _tracks.Values)
        {
            if (string.Equals(track.OwnerEmail, oldEmail, StringComparison.OrdinalIgnoreCase))
            {
                track.OwnerEmail = newEmail;
            }
        }
    }

    public IReadOnlyCollection<TrackingItem> GetDueTracks(DateTimeOffset now)
    {
        var today = (schedule ?? TrackingSchedule.Utc).Today(now);
        return _tracks.Values.Where(item =>
        {
            if (!item.IsActive) return false;
            if (item.EndDate.HasValue && item.EndDate.Value < today) { item.IsActive = false; return false; }
            if (item.StartDate.HasValue && item.StartDate.Value > today) return false;
            var lastCheck = item.LastCheckedAt ?? item.CreatedAt;
            return now - lastCheck >= TimeSpan.FromMinutes(item.CheckIntervalMinutes);
        }).ToArray();
    }

    public void RecordPriceCheck(string trackingId, decimal? currentPrice, DateTimeOffset checkedAt, string? error = null) =>
        RecordPriceCheck(trackingId, currentPrice, null, checkedAt, error);

    public void RecordPriceCheck(string trackingId, decimal? currentPrice, bool? isInStock, DateTimeOffset checkedAt, string? error = null)
    {
        if (_tracks.TryGetValue(trackingId, out var item))
        {
            item.CurrentPrice = currentPrice;
            item.IsInStock = isInStock;
            item.LastCheckedAt = checkedAt;
            item.LastCheckError = error;
        }
    }

    public void MarkNotified(string trackingId, DateTimeOffset notifiedAt, string messageSid)
    {
        if (_tracks.TryGetValue(trackingId, out var item))
        {
            item.IsNotified = true;
            item.NotifiedAt = notifiedAt;
            item.NotificationMessageSid = messageSid;
            item.LastAlertError = null;
            if (item.DisableAfterNotification) item.IsActive = false;
        }
    }

    public void RecordAlertFailure(string trackingId, string error)
    {
        if (_tracks.TryGetValue(trackingId, out var item)) item.LastAlertError = error;
    }

    public IReadOnlyDictionary<string, string> GetNotificationReceipts(string trackingId) =>
        receipts.TryGetValue(trackingId, out var saved) ? new Dictionary<string, string>(saved) : new Dictionary<string, string>();

    public void RecordNotificationReceipt(string trackingId, string channel, string messageId)
    {
        if (_tracks.ContainsKey(trackingId))
            receipts.GetOrAdd(trackingId, _ => new())[channel] = messageId;
    }

    public bool Toggle(string trackingId, string ownerEmail)
    {
        if (!_tracks.TryGetValue(trackingId, out var item) || !string.Equals(item.OwnerEmail, ownerEmail, StringComparison.OrdinalIgnoreCase)) return false;
        item.IsActive = !item.IsActive;
        return true;
    }
}
