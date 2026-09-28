using Dudu.Core.Time;

namespace Dudu.Core.Pet;

/// <summary>How Dudu reacts to a single pet.</summary>
public enum PetReaction
{
    /// <summary>An ordinary pet: the one-shot <c>petted</c> clip.</summary>
    Petted,

    /// <summary>A quick streak of pets: the one-shot <c>celebrate</c> clip.</summary>
    Delighted,

    /// <summary>Poked a bit too much: the one-shot <c>grumpy</c> clip.</summary>
    Grumpy,

    /// <summary>Still being poked: the angry stomping <c>tantrum</c> clip.</summary>
    Angry,

    /// <summary>Poked on and on: the rolling-on-the-floor <c>flail</c> clip.</summary>
    Flailing,

    /// <summary>The end of a poke streak: the one-shot <c>wail</c> clip. He
    /// cries it out, and the next poke starts over.</summary>
    Wailing,
}

/// <summary>
/// Pure, clock-injected record of how much Dudu has been petted.
/// <para>
/// Neglect is measured in <em>active</em> time only: the presentation tick
/// reports each ~30 s whether Dudu is actually on screen and allowed to act
/// (not hidden, paused, locked, fullscreen, eating, or dragged).
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
/// <para>
/// Pokes that keep coming, each within <see cref="PokeStreakGap"/> (6 s) of
/// the one before, wear him down: from the <see cref="GrumpyPokeCount"/>th
/// (4th) he is grumpy, from the 6th angry, from the 8th flailing, and the
/// 10th makes him wail, which ends the streak. A calmer gap starts it over.
/// </para>
/// </summary>
public sealed class AffectionTracker
{
    public static readonly TimeSpan NeglectThreshold = TimeSpan.FromMinutes(90);
    public static readonly TimeSpan TantrumCooldown = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxObservationGap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DelightWindow = TimeSpan.FromMinutes(1);
    public const int DelightPetCount = 3;
    public static readonly TimeSpan PokeStreakGap = TimeSpan.FromSeconds(6);
    public const int GrumpyPokeCount = 4;
    public const int AngryPokeCount = 6;
    public const int FlailPokeCount = 8;
    public const int WailPokeCount = 10;

    private readonly object _sync = new();
    private readonly IClock _clock;
    private readonly List<DateTimeOffset> _recentPets = [];
    private TimeSpan _neglected;
    private DateTimeOffset? _lastObservedUtc;
    private bool _lastObservedActive;
    private DateTimeOffset? _lastTantrumUtc;
    private DateTimeOffset? _lastPetUtc;
    private int _pokeStreak;

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
    /// Records a pet: resets the neglect clock and reports how he takes it —
    /// a delight streak, or a poke too many.
    /// </summary>
    public PetReaction RecordPet()
    {
        var now = _clock.UtcNow;
        lock (_sync)
        {
            _neglected = TimeSpan.Zero;
            _lastTantrumUtc = null;
            _pokeStreak = _lastPetUtc is { } lastPet && now >= lastPet && now - lastPet < PokeStreakGap
                ? _pokeStreak + 1
                : 1;
            _lastPetUtc = now;
            if (_pokeStreak >= GrumpyPokeCount)
            {
                // Annoyed pokes never count towards a later delight streak.
                _recentPets.Clear();
                if (_pokeStreak >= WailPokeCount)
                {
                    _pokeStreak = 0;
                    _lastPetUtc = null;
                    return PetReaction.Wailing;
                }

                return _pokeStreak >= FlailPokeCount ? PetReaction.Flailing
                    : _pokeStreak >= AngryPokeCount ? PetReaction.Angry
                    : PetReaction.Grumpy;
            }

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
