using Dudu.App.Audio;
using Xunit;

namespace Dudu.App.Tests.Audio;

public sealed class WavPcmTests
{
    [Fact]
    public void Finds_the_data_chunk_after_a_padded_list_chunk()
    {
        var wave = TestWaves.Pcm16(1, 2, 3);

        Assert.True(WavPcm.TryFindData(wave, out var offset, out var length));
        Assert.Equal(TestWaves.DataOffset(wave), offset);
        Assert.Equal(6, length);
    }

    [Fact]
    public void Rejects_buffers_without_a_riff_wave_data_chunk()
    {
        Assert.False(WavPcm.TryFindData("not-a-wave"u8, out _, out _));
        var truncated = TestWaves.Pcm16(1, 2, 3)[..40];
        Assert.False(WavPcm.TryFindData(truncated, out _, out _));
        Assert.Null(WavPcm.CreateScaledCopy(truncated, 0.5));
        Assert.Equal(0, WavPcm.PeakLevel(truncated));
    }

    [Fact]
    public void Peak_level_flags_digital_silence_and_near_silence()
    {
        // Regression: three of the five shipped cues were all-zero samples
        // and the other two peaked at -42 / -59 dBFS, so "sound on" played
        // nothing audible.
        Assert.Equal(0, WavPcm.PeakLevel(TestWaves.Pcm16(0, 0, 0, 0)));
        Assert.True(WavPcm.PeakLevel(TestWaves.Pcm16(0, 268, -120)) < WavPcm.AudiblePeakThreshold);
        Assert.Equal(0.5, WavPcm.PeakLevel(TestWaves.Pcm16(100, -16384, 0)));
        Assert.Equal(1.0, WavPcm.PeakLevel(TestWaves.Pcm16(short.MinValue)));
    }

    [Fact]
    public void Scaled_copy_applies_linear_gain_and_leaves_the_source_untouched()
    {
        var wave = TestWaves.Pcm16(1000, -1000, short.MaxValue, short.MinValue);

        var scaled = WavPcm.CreateScaledCopy(wave, 0.5)!;

        Assert.Equal(wave.Length, scaled.Length);
        Assert.Equal(wave[..TestWaves.DataOffset(wave)], scaled[..TestWaves.DataOffset(wave)]);
        Assert.Equal(new short[] { 500, -500, 16384, -16384 }, TestWaves.ReadSamples(scaled, 4));
        Assert.Equal(new short[] { 1000, -1000, short.MaxValue, short.MinValue }, TestWaves.ReadSamples(wave, 4));
    }

    [Theory]
    [InlineData(2.0, 1000)]
    [InlineData(1.0, 1000)]
    [InlineData(-1.0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Scaled_copy_clamps_gain_to_unit_range(double volume, short expected)
    {
        var scaled = WavPcm.CreateScaledCopy(TestWaves.Pcm16(1000), volume)!;

        Assert.Equal(expected, TestWaves.ReadSamples(scaled, 1)[0]);
    }

    [Fact]
    public void Scaled_copy_is_allocated_pinned_for_the_async_native_player()
    {
        var scaled = WavPcm.CreateScaledCopy(TestWaves.Pcm16(1, 2), 0.5)!;

        // Pinned-object-heap arrays report generation 2 and never move; the
        // player hands their address to winmm, which reads it after the
        // call returns.
        Assert.Equal(GC.MaxGeneration, GC.GetGeneration(scaled));
    }
}
