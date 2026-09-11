using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Policies;
using Dudu.Core.Time;

namespace Dudu.Core.Pet;

public sealed class AmbientScheduler
{
    private static readonly string[] AnimationKeys = ["idle", "blink", "wave", "sleep"];
    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly QuietHours _quietHours;
    private readonly TimeSpan _minimumInterval;

    public AmbientScheduler(IClock clock, IRandomSource random, Preferences preferences)
        : this(clock, random, preferences.AmbientMinimumInterval, preferences.QuietHours)
    {
    }

    public AmbientScheduler(
        IClock clock,
        IRandomSource random,
        TimeSpan ambientMinimumInterval,
        QuietHours? quietHours = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        if (ambientMinimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ambientMinimumInterval));
        }

        _minimumInterval = ambientMinimumInterval;
        _quietHours = quietHours ?? new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue);
        NextEligibleUtc = _clock.UtcNow;
    }

    public DateTimeOffset NextEligibleUtc { get; private set; }

    public PetEvent? TryGetNextEvent(
        bool paused,
        bool focusActive,
        bool fullscreen,
        bool sessionLocked)
    {
        var now = _clock.UtcNow;
        if (paused || focusActive || fullscreen || sessionLocked)
        {
            return null;
        }

        if (now < NextEligibleUtc
            || QuietHoursPolicy.IsQuiet(now, _quietHours, _clock.LocalTimeZone))
        {
            return null;
        }

        var animationKey = AnimationKeys[NextRandom(AnimationKeys.Length)];
        var randomDelay = TimeSpan.FromMinutes(15 + NextRandom(31));
        var delay = randomDelay < _minimumInterval ? _minimumInterval : randomDelay;
        NextEligibleUtc = now + delay;
        return new PetEvent.AmbientRequested(animationKey);
    }

    public PetEvent? TryCreateEvent(
        bool paused,
        bool focusActive,
        bool fullscreen,
        bool sessionLocked)
    {
        return TryGetNextEvent(paused, focusActive, fullscreen, sessionLocked);
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
