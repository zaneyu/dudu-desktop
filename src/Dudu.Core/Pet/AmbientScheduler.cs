using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Policies;
using Dudu.Core.Time;

namespace Dudu.Core.Pet;

public sealed class AmbientScheduler
{
    // Keep ambient selection aligned with the shipped private pack. A missing
    // animation silently falls back to idle, which makes Dudu feel broken even
    // though the state machine appears to be moving.
    private const string StickerSentinel = "sticker";
    private static readonly string[] AnimationKeys =
        ["idle", "blink", "greeting", "sleep", "drink", "celebrate", StickerSentinel];
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

        // A fresh scheduler has shown nothing yet, so the first ambient moment is
        // one minimum interval out — not immediately at startup, which would greet
        // the user with motion before the app has even settled.
        NextEligibleUtc = _clock.UtcNow + _minimumInterval;
    }

    public DateTimeOffset NextEligibleUtc { get; private set; }

    /// <summary>
    /// Attempts to produce the next ambient pet moment.
    /// </summary>
    /// <param name="quietHoursOverride">
    /// Explicit quiet-hours verdict from the caller. When null, the scheduler consults
    /// <see cref="QuietHoursPolicy"/> itself; when set, the caller's value wins.
    /// Callers must derive the override from the same policy (same preferences, clock,
    /// and time zone) — a stale <c>false</c> would bypass quiet hours and a stale
    /// <c>true</c> would suppress ambience that should play. The only audited caller is
    /// PresentationCoordinator, which passes its per-tick <c>NowQuiet</c> snapshot taken
    /// from the same quiet-hours source on the same tick.
    /// </param>
    /// <param name="availableStickerKeys">
    /// The sticker animation keys the active pack actually ships. When null or empty,
    /// falls back to rolling uniformly over the full 1..<see cref="AssetManifestContract.StickerAnimationCount"/>
    /// range — the scheduler itself has no access to the loaded pack, so a caller that
    /// does (<c>PresentationCoordinator</c>) passes the pack's real keys here to avoid
    /// rolling a number the pack has no art for, which silently falls back to idle.
    /// </param>
    public PetEvent? TryGetNextEvent(
        bool paused,
        bool focusActive,
        bool fullscreen,
        bool sessionLocked,
        bool? quietHoursOverride = null,
        IReadOnlyList<string>? availableStickerKeys = null)
    {
        var now = _clock.UtcNow;
        if (paused || focusActive || fullscreen || sessionLocked)
        {
            return null;
        }

        if (now < NextEligibleUtc
            || (quietHoursOverride ?? QuietHoursPolicy.IsQuiet(
                now,
                _quietHours,
                _clock.LocalTimeZone)))
        {
            return null;
        }

        var selectedAnimation = AnimationKeys[NextRandom(AnimationKeys.Length)];
        var animationKey = selectedAnimation == StickerSentinel
            ? SelectStickerKey(availableStickerKeys)
            : selectedAnimation;
        var randomDelay = TimeSpan.FromMinutes(15 + NextRandom(31));
        var delay = randomDelay < _minimumInterval ? _minimumInterval : randomDelay;
        NextEligibleUtc = now + delay;
        return new PetEvent.AmbientRequested(animationKey);
    }

    private string SelectStickerKey(IReadOnlyList<string>? availableStickerKeys)
    {
        if (availableStickerKeys is { Count: > 0 })
        {
            return availableStickerKeys[NextRandom(availableStickerKeys.Count)];
        }

        return AssetManifestContract.StickerAnimationKey(NextRandom(AssetManifestContract.StickerAnimationCount) + 1);
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
