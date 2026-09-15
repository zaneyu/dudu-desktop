using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Remote;

/// <summary>
/// Full-jitter exponential backoff for the poll loop: base delay doubles each failed attempt,
/// capped at <see cref="CapSeconds"/>, then scaled by a uniform random factor in [0.5, 1.5) so a
/// fleet of desktops does not retry in lockstep. <see cref="Reset"/> returns to the first attempt
/// after a success. The exponent is clamped at <see cref="MaxExponent"/> so an outage lasting
/// longer than the attempt counter's range can never overflow into a negative delay.
/// <see cref="SteadyDelay"/> separately jitters the healthy steady-poll interval by ±20% so
/// healthy desktops do not poll in lockstep either.
/// </summary>
public sealed class PollBackoff
{
    private const double BaseSeconds = 5;
    private const double CapSeconds = 60;

    /// <summary>
    /// 5 * 2^10 = 5120s, far above <see cref="CapSeconds"/>: the counter saturates here instead
    /// of overflowing <see cref="int"/> after ~2 billion consecutive failures.
    /// </summary>
    private const int MaxExponent = 10;

    /// <summary>The healthy poll cadence; jittered by <see cref="SteadyDelay"/>.</summary>
    public const double SteadyPollIntervalSeconds = 15;

    /// <summary>Steady-poll jitter band: the healthy interval varies by ±20%.</summary>
    private const double SteadyJitterFraction = 0.2;

    private const int JitterResolution = 1_000_000;

    private readonly IRandomSource _random;
    private int _attempt;

    public PollBackoff(IRandomSource random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public TimeSpan NextDelay()
    {
        var exponent = Math.Min(_attempt, MaxExponent);
        var baseDelaySeconds = Math.Min(BaseSeconds * Math.Pow(2, exponent), CapSeconds);
        _attempt = Math.Min(_attempt + 1, MaxExponent);

        var fraction = _random.Next(JitterResolution) / (double)JitterResolution;
        return TimeSpan.FromSeconds(baseDelaySeconds * (0.5 + fraction));
    }

    /// <summary>
    /// The healthy steady-poll interval (<see cref="SteadyPollIntervalSeconds"/>s) jittered by
    /// ±20%. Unlike <see cref="NextDelay"/>, this never advances the failure counter.
    /// </summary>
    public TimeSpan SteadyDelay()
    {
        var fraction = _random.Next(JitterResolution) / (double)JitterResolution;
        return TimeSpan.FromSeconds(
            SteadyPollIntervalSeconds * (1 - SteadyJitterFraction + (2 * SteadyJitterFraction * fraction)));
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
