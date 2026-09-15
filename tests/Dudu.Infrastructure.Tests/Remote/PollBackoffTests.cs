using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Remote;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

public sealed class PollBackoffTests
{
    [Fact]
    public void Backoff_is_capped_jittered_and_resets_after_success()
    {
        var random = new SequenceRandomSource(0.5, 0.5, 0.5, 0.5, 0.5);
        var backoff = new PollBackoff(random);

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
             TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)],
            Enumerable.Range(0, 5).Select(_ => backoff.NextDelay()).ToArray());
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
    }

    [Theory]
    [InlineData(0.0, 12)]
    [InlineData(0.5, 15)]
    [InlineData(0.999999, 18)]
    public void Steady_delay_jitters_the_healthy_interval_by_plus_or_minus_twenty_percent(
        double fraction, double expectedSeconds)
    {
        var backoff = new PollBackoff(new SequenceRandomSource(fraction));

        var actual = backoff.SteadyDelay();
        Assert.True(
            Math.Abs((actual - TimeSpan.FromSeconds(expectedSeconds)).TotalMilliseconds) <= 1,
            $"expected ~{expectedSeconds}s but got {actual}");
    }

    [Fact]
    public void Steady_delay_never_advances_the_failure_counter()
    {
        var backoff = new PollBackoff(new SequenceRandomSource(0.5));

        backoff.SteadyDelay();
        backoff.SteadyDelay();

        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
    }

    [Fact]
    public void Backoff_exponent_is_clamped_and_never_goes_negative()
    {
        // An outage outlasting the attempt counter's range must sit at the cap, not overflow
        // into a negative delay.
        var backoff = new PollBackoff(new SequenceRandomSource(0.5));

        var delays = Enumerable.Range(0, 30).Select(_ => backoff.NextDelay()).ToArray();

        Assert.All(delays, delay => Assert.True(delay >= TimeSpan.Zero));
        Assert.All(delays.Skip(4), delay => Assert.Equal(TimeSpan.FromSeconds(60), delay));
    }

    /// <summary>
    /// Deterministic <see cref="IRandomSource"/> test double: each call to <see cref="Next"/>
    /// consumes the next configured fraction (mapping it onto [0, exclusiveMax)), repeating the
    /// last fraction once the configured sequence is exhausted.
    /// </summary>
    private sealed class SequenceRandomSource : IRandomSource
    {
        private readonly double[] _fractions;
        private int _index;

        public SequenceRandomSource(params double[] fractions)
        {
            ArgumentNullException.ThrowIfNull(fractions);
            if (fractions.Length == 0) throw new ArgumentException("At least one fraction is required.", nameof(fractions));
            _fractions = fractions;
        }

        public int Next(int exclusiveMax)
        {
            var fraction = _fractions[Math.Min(_index, _fractions.Length - 1)];
            _index++;
            return (int)(fraction * exclusiveMax);
        }
    }
}
