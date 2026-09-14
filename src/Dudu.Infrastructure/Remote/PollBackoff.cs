using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Full-jitter exponential backoff for the poll loop: base delay doubles each failed attempt,
/// capped at <see cref="CapSeconds"/>, then scaled by a uniform random factor in [0.5, 1.5) so a
/// fleet of desktops does not retry in lockstep. <see cref="Reset"/> returns to the first attempt
/// after a success.
/// </summary>
public sealed class PollBackoff
{
    private const double BaseSeconds = 5;
    private const double CapSeconds = 60;
    private const int JitterResolution = 1_000_000;

    private readonly IRandomSource _random;
    private int _attempt;

    public PollBackoff(IRandomSource random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public TimeSpan NextDelay()
    {
        var baseDelaySeconds = Math.Min(BaseSeconds * Math.Pow(2, _attempt), CapSeconds);
        _attempt++;

        var fraction = _random.Next(JitterResolution) / (double)JitterResolution;
        return TimeSpan.FromSeconds(baseDelaySeconds * (0.5 + fraction));
    }

    /// <summary>
    /// The fully-backed-off delay (the <see cref="CapSeconds"/> cap, jittered the same way)
    /// without advancing the attempt counter. Used for a failure the poll loop treats as
    /// terminal for this iteration -- there is nothing to ramp up to, so it waits the cap
    /// immediately instead of tight-looping through the early, short delays.
    /// </summary>
    public TimeSpan MaxDelay()
    {
        var fraction = _random.Next(JitterResolution) / (double)JitterResolution;
        return TimeSpan.FromSeconds(CapSeconds * (0.5 + fraction));
    }

    public void Reset() => _attempt = 0;
}
