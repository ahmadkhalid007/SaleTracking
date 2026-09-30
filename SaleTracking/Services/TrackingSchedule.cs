using SaleTracking.Models;

namespace SaleTracking.Services;

public sealed class TrackingSchedule(TimeZoneInfo timeZone)
{
    public static TrackingSchedule Utc { get; } = new(TimeZoneInfo.Utc);
    public string DisplayName => timeZone.Id == "Asia/Karachi" || timeZone.Id == "Pakistan Standard Time"
        ? "Pakistan time (UTC+05:00)" : timeZone.DisplayName;
    public DateTime LocalTime(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(LocalTime(now));

    public bool CanAlert(TrackingItem track, DateTimeOffset now)
    {
        if (!track.IsActive) return false;
        var local = LocalTime(now);
        var today = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        if (track.StartDate > today || track.EndDate < today) return false;

        // Without dates, both times describe a daily window, including overnight windows.
        if (track.StartDate is null && track.EndDate is null &&
            track.StartTime is { } start && track.EndTime is { } end && start > end)
            return time >= start || time <= end;

        if (track.StartTime is { } startTime && (track.StartDate is null || track.StartDate == today) && time < startTime)
            return false;
        if (track.EndTime is { } endTime && (track.EndDate is null || track.EndDate == today) && time > endTime)
            return false;
        return true;
    }
}
