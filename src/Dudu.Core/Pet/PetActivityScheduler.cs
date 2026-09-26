using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Time;

namespace Dudu.Core.Pet;

public enum PetActivityKind
{
    /// <summary>Plays one motion clip in place.</summary>
    Fidget,

    /// <summary>Plays the walk clip while the pet travels sideways.</summary>
    Wander,
}

/// <summary>
/// One short, silent bit of unsolicited motion between notes. A wander carries a
/// preferred horizontal <see cref="Direction"/> (-1 left, +1 right) and a travel
/// <see cref="Distance"/> in nominal (unscaled) art pixels; the overlay decides
/// whether there is room and may turn around at a screen edge.
/// </summary>
public sealed record PetActivity(PetActivityKind Kind, string AnimationKey, int Direction, int Distance)
{
    public static PetActivity Fidget(string animationKey) =>
        new(PetActivityKind.Fidget, animationKey, 0, 0);

    public static PetActivity Wander(int direction, int distance) =>
        new(PetActivityKind.Wander, AssetManifestContract.WalkAnimationKey, direction, distance);
}

/// <summary>
/// Everything that must keep Dudu still. Reduced motion turns idle activity off
/// entirely; the rest are the same environment gates the ambient scheduler honors,
/// plus <see cref="Busy"/> for "the pet is presenting something or the user is
/// interacting with it".
/// </summary>
public readonly record struct PetActivityGate(
    bool ReducedMotion,
    bool Paused,
    bool Fullscreen,
    bool SessionLocked,
    bool Hidden,
    bool QuietHours,
    bool Busy)
{
    public static PetActivityGate Open => default;

    public bool IsSuppressed =>
        ReducedMotion || Paused || Fullscreen || SessionLocked || Hidden || QuietHours || Busy;
}

/// <summary>
/// Picks frequent idle fidgets and short wanders so the pet keeps moving between
/// the rarer, note-backed ambient moments of <see cref="AmbientScheduler"/>. Only
/// clips the loaded pack actually ships are ever chosen.
/// </summary>
public sealed class PetActivityScheduler
{
    /// <summary>Quiet time after startup before the first activity.</summary>
    public static readonly TimeSpan FirstActivityDelay = TimeSpan.FromSeconds(45);

    /// <summary>Shortest gap between activities, and the settle time after any suppression.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(25);

    /// <summary>Extra random gap on top of <see cref="MinimumInterval"/> (0 through this many seconds).</summary>
    public const int IntervalJitterSeconds = 50;

    /// <summary>Chance (percent) that an activity is a wander when both kinds are available.</summary>
    public const int WanderPercent = 40;

    public const int MinimumWanderDistance = 80;
    public const int MaximumWanderDistance = 240;

    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly string[] _fidgetKeys;
    private readonly bool _canWander;
    private string? _lastFidgetKey;

    /// <param name="availableAnimationKeys">
    /// The animation keys the active pack ships. Motion clips missing from it are
    /// never requested, so the neutral fallback pack (which has none) never moves.
    /// </param>
    public PetActivityScheduler(
        IClock clock,
        IRandomSource random,
        IEnumerable<string> availableAnimationKeys)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        ArgumentNullException.ThrowIfNull(availableAnimationKeys);
        var available = new HashSet<string>(availableAnimationKeys, StringComparer.Ordinal);
        _fidgetKeys = AssetManifestContract.MotionAnimationKeys
            .Where(key => !string.Equals(key, AssetManifestContract.WalkAnimationKey, StringComparison.Ordinal))
            .Where(available.Contains)
            .ToArray();
        _canWander = available.Contains(AssetManifestContract.WalkAnimationKey);
        NextEligibleUtc = _clock.UtcNow + FirstActivityDelay;
    }

    public DateTimeOffset NextEligibleUtc { get; private set; }

    public IReadOnlyList<string> FidgetKeys => _fidgetKeys;

    public bool CanWander => _canWander;

    public bool HasActivities => _canWander || _fidgetKeys.Length > 0;

    /// <summary>
    /// Returns the next activity once one is due and nothing suppresses motion.
    /// While suppressed, the next activity is held at least <see cref="MinimumInterval"/>
    /// out so Dudu settles for a moment after a note, a fullscreen app, or an unlock
    /// instead of fidgeting the instant the gate reopens.
    /// </summary>
    public PetActivity? TryGetNext(PetActivityGate gate)
    {
        var now = _clock.UtcNow;
        if (gate.IsSuppressed)
        {
            var settled = now + MinimumInterval;
            if (settled > NextEligibleUtc)
            {
                NextEligibleUtc = settled;
            }

            return null;
        }

        if (now < NextEligibleUtc || !HasActivities)
        {
            return null;
        }

        var wander = _canWander
            && (_fidgetKeys.Length == 0 || NextRandom(100) < WanderPercent);
        PetActivity activity;
        if (wander)
        {
            var direction = NextRandom(2) == 0 ? -1 : 1;
            var distance = MinimumWanderDistance
                + NextRandom(MaximumWanderDistance - MinimumWanderDistance + 1);
            activity = PetActivity.Wander(direction, distance);
        }
        else
        {
            var index = NextRandom(_fidgetKeys.Length);
            if (_fidgetKeys.Length > 1
                && string.Equals(_fidgetKeys[index], _lastFidgetKey, StringComparison.Ordinal))
            {
                // Never the same clip twice in a row.
                index = (index + 1) % _fidgetKeys.Length;
            }

            _lastFidgetKey = _fidgetKeys[index];
            activity = PetActivity.Fidget(_lastFidgetKey);
        }

        NextEligibleUtc = now + MinimumInterval + TimeSpan.FromSeconds(NextRandom(IntervalJitterSeconds + 1));
        return activity;
    }

    private int NextRandom(int exclusiveMax)
    {
        var value = _random.Next(exclusiveMax);
        if (value < 0 || value >= exclusiveMax)
        {
            throw new InvalidOperationException(
                $"Random source returned {value}; expected a value from 0 through {exclusiveMax - 1}.");
        }

        return value;
    }
}
