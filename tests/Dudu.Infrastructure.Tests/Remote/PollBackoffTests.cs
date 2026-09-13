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
