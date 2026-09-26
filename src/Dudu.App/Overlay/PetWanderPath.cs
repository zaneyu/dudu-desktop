namespace Dudu.App.Overlay;

/// <summary>A planned sideways walk of the pet window, in screen pixels.</summary>
public readonly record struct OverlayWanderPlan(PixelRect Start, PixelRect Target)
{
    public int Direction => Math.Sign(Target.X - Start.X);
}

/// <summary>
/// Pure geometry for the pet's idle wander: where a walk may go inside the
/// monitor's work area and where the window sits part-way through it.
/// </summary>
internal static class PetWanderPath
{
    /// <summary>Shortest walk worth taking, in window pixels. With less room
    /// than this in both directions the pet stays where it is.</summary>
    internal const int MinimumTravel = 24;

    /// <summary>
    /// Plans a horizontal walk of up to <paramref name="distance"/> pixels,
    /// preferring <paramref name="preferredDirection"/> (-1 left, +1 right) and
    /// turning around when that side of the work area has too little room. The
    /// window never leaves the work area, and its vertical position and size
    /// are unchanged.
    /// </summary>
    internal static OverlayWanderPlan? Plan(
        PixelRect bounds,
        PixelRect workArea,
        int preferredDirection,
        int distance)
    {
        if (!bounds.IsValid || !workArea.IsValid || distance <= 0)
        {
            return null;
        }

        var roomLeft = Math.Max(0L, (long)bounds.X - workArea.X);
        var roomRight = Math.Max(0L, (long)workArea.Right - bounds.Right);
        var direction = preferredDirection < 0 ? -1 : 1;
        var room = direction < 0 ? roomLeft : roomRight;
        if (room < MinimumTravel)
        {
            direction = -direction;
            room = direction < 0 ? roomLeft : roomRight;
            if (room < MinimumTravel)
            {
                return null;
            }
        }

        var travel = (int)Math.Min(distance, room);
        return new OverlayWanderPlan(bounds, bounds with { X = bounds.X + (direction * travel) });
    }

    /// <summary>
    /// The window position <paramref name="progress"/> (0..1) of the way along
    /// <paramref name="plan"/>, eased in and out so the walk starts and stops softly.
    /// </summary>
    internal static PixelRect At(OverlayWanderPlan plan, double progress)
    {
        var t = double.IsFinite(progress) ? Math.Clamp(progress, 0d, 1d) : 1d;
        var eased = t * t * (3d - (2d * t));
        var offset = (int)Math.Round(
            (plan.Target.X - (double)plan.Start.X) * eased,
            MidpointRounding.AwayFromZero);
        return plan.Start with { X = plan.Start.X + offset };
    }

    /// <summary>
    /// Steps the window along <paramref name="plan"/> over
    /// <paramref name="duration"/>. <paramref name="tryStepAsync"/> moves the
    /// window from the position it should be at to the next one and returns
    /// false when something else moved or hid it, which ends the walk where it
    /// is. <paramref name="commitAsync"/> always runs afterwards so wherever
    /// the pet stopped is saved. Returns true only when the whole walk completed.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on <see cref="OverlayWindowHost"/> because that
    /// class is <c>unsafe</c> and C# cannot await in an unsafe context.
    /// </remarks>
    internal static async Task<bool> GlideAsync(
        OverlayWanderPlan plan,
        TimeSpan duration,
        TimeSpan stepInterval,
        Func<PixelRect, PixelRect, CancellationToken, Task<bool>> tryStepAsync,
        Func<Task> commitAsync,
        Action<Exception> reportFailure,
        Action<Exception> reportCommitFailure,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tryStepAsync);
        ArgumentNullException.ThrowIfNull(commitAsync);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(reportCommitFailure);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        delayAsync ??= Task.Delay;
        var stopwatch = global::System.Diagnostics.Stopwatch.StartNew();
        var expected = plan.Start;
        try
        {
            while (true)
            {
                var progress = Math.Min(1d, stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
                var next = At(plan, progress);
                if (!await tryStepAsync(expected, next, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                expected = next;
                if (progress >= 1d)
                {
                    return true;
                }

                await delayAsync(stepInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reportFailure(exception);
            return false;
        }
        finally
        {
            try
            {
                await commitAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                reportCommitFailure(exception);
            }
        }
    }
}
