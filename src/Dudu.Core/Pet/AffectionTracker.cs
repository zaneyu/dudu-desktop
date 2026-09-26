using Dudu.Core.Time;

namespace Dudu.Core.Pet;

/// <summary>How Dudu reacts to a single pet.</summary>
public enum PetReaction
{
    /// <summary>An ordinary pet: the one-shot <c>petted</c> clip.</summary>
    Petted,

    /// <summary>A quick streak of pets: the one-shot <c>celebrate</c> clip.</summary>
    Delighted,
}

/// <summary>
/// Pure, clock-injected record of how much Dudu has been petted.
/// <para>
/// Neglect is measured in <em>active</em> time only: the presentation tick
/// reports each ~30 s whether Dudu is actually on screen and allowed to act
/// (not hidden, paused, locked, fullscreen, in quiet hours, focus, or a meal).
/// Time while inactive — and any gap between observations longer than
/// <see cref="MaxObservationGap"/> (sleep, a hung tick, the app closed) — does
/// not count, so unlocking the PC in the morning never triggers an instant
/// tantrum for the night before.
/// </para>
/// <para>
/// Constants: a tantrum becomes due after <see cref="NeglectThreshold"/>
/// (90 min) of active time since the last pet (or since start-up), and at
/// most once per <see cref="TantrumCooldown"/> (30 min) while still
/// neglected. <see cref="DelightPetCount"/> (3) pets within
/// <see cref="DelightWindow"/> (1 min) make him delighted; the streak then
/// starts over so every third quick pet celebrates, not every pet after it.
/// </para>
/// </summary>
public sealed class AffectionTracker
{
    public static readonly TimeSpan NeglectThreshold = TimeSpan.FromMinutes(90);
    public static readonly TimeSpan TantrumCooldown = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxObservationGap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DelightWindow = TimeSpan.FromMinutes(1);
    public const int DelightPetCount = 3;

    private readonly object _sync = new();
    private readonly IClock _clock;
    private readonly List<DateTimeOffset> _recentPets = [];
    private TimeSpan _neglected;
    private DateTimeOffset? _lastObservedUtc;
    private bool _lastObservedActive;
    private DateTimeOffset? _lastTantrumUtc;

    public AffectionTracker(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Active time accumulated since the last pet.</summary>
    public TimeSpan NeglectedFor
    {
        get
        {
            lock (_sync)
            {
                return _neglected;
            }
        }
    }

    /// <summary>
    /// Records a pet: resets the neglect clock and reports whether this pet
    /// completed a delight streak.
    /// </summary>
    public PetReaction RecordPet()
    {
        var now = _clock.UtcNow;
        lock (_sync)
        {
            _neglected = TimeSpan.Zero;
            _lastTantrumUtc = null;
            _recentPets.RemoveAll(pet => now - pet >= DelightWindow || pet > now);
            _recentPets.Add(now);
            if (_recentPets.Count < DelightPetCount)
            {
                return PetReaction.Petted;
            }

            _recentPets.Clear();
            return PetReaction.Delighted;
        }
    }

    /// <summary>
    /// Records one presentation-tick observation and reports whether a
    /// tantrum is due now. Does not consume the tantrum: call
    /// <see cref="RecordTantrum"/> once it is actually shown, so a tick that
    /// had something more important to present simply retries next tick.
    /// </summary>
    /// <param name="active">Whether Dudu is on screen and free to act.</param>
    public bool Observe(bool active)
    {
        var now = _clock.UtcNow;
        lock (_sync)
        {
            if (active
                && _lastObservedActive
                && _lastObservedUtc is { } last
                && now > last
                && now - last <= MaxObservationGap)
            {
                _neglected += now - last;
            }

            _lastObservedUtc = now;
            _lastObservedActive = active;
            return active
                && _neglected >= NeglectThreshold
                && (_lastTantrumUtc is not { } previous || now - previous >= TantrumCooldown || now < previous);
        }
    }

    /// <summary>Marks a tantrum as shown, starting the cooldown.</summary>
    public void RecordTantrum()
    {
        var now = _clock.UtcNow;
        lock (_sync)
        {
            _lastTantrumUtc = now;
        }
    }
}
