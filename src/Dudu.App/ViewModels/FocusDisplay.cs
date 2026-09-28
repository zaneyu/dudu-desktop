using Dudu.Core.Models;

namespace Dudu.App.ViewModels;

/// <summary>The one focus status line shared by the Tasks and Focus page and Home, so
/// both surfaces word a session the same way (and neither prints the raw enum).</summary>
public static class FocusDisplay
{
    /// <param name="remaining">Time left to show; the Tasks page passes a live,
    /// ticking value, Home the snapshot's own <see cref="FocusSnapshot.Remaining"/>.</param>
    public static string Describe(FocusSnapshot? focus, TimeSpan remaining) => focus switch
    {
        null => "no focus running",
        { Status: FocusStatus.Running } => $"focus is running with {FormatRemaining(remaining)} left",
        { Status: FocusStatus.Paused } => $"focus is paused with {FormatRemaining(remaining)} left",
        { Status: FocusStatus.Completed } => "last focus session completed le",
        _ => "last focus session ended early",
    };

    /// <summary>Time left right now for a snapshot read at <paramref name="capturedUtc"/>:
    /// a running session counts down from it; a paused one holds still. Never negative.</summary>
    public static TimeSpan RemainingAt(FocusSnapshot? focus, DateTimeOffset capturedUtc, DateTimeOffset nowUtc)
    {
        if (focus is null) return TimeSpan.Zero;
        if (focus.Status == FocusStatus.Paused) return focus.Remaining;
        if (focus.Status != FocusStatus.Running) return TimeSpan.Zero;
        var elapsed = nowUtc.ToUniversalTime() - capturedUtc.ToUniversalTime();
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var remaining = focus.Remaining - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Rounds up to whole minutes (so a session never reads "0 min"
    /// while seconds remain) and switches to hours past an hour.</summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        var totalMinutes = remaining <= TimeSpan.Zero
            ? 0
            : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMinutes));
        if (totalMinutes < 60) return $"{totalMinutes} min";
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0 ? $"{hours} hr" : $"{hours} hr {minutes} min";
    }
}
