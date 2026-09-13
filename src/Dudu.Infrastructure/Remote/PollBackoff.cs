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

    public void Reset() => _attempt = 0;
}
