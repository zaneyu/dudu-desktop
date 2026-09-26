using System.Buffers.Binary;

namespace Dudu.App.Audio;

/// <summary>
/// Pure helpers over an already-validated RIFF/WAVE, 16-bit PCM buffer (see
/// <see cref="AudioManifestLoader"/>). No platform dependency, so the byte
/// maths stays testable off Windows.
/// </summary>
public static class WavPcm
{
    /// <summary>A cue whose loudest sample is below this linear peak
    /// (about -30 dBFS) is effectively inaudible at the default volume.</summary>
    public const double AudiblePeakThreshold = 0.03;

    /// <summary>Locates the <c>data</c> chunk of a RIFF/WAVE buffer.</summary>
    public static bool TryFindData(ReadOnlySpan<byte> wave, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (wave.Length < 12 || !wave[..4].SequenceEqual("RIFF"u8) || !wave.Slice(8, 4).SequenceEqual("WAVE"u8))
            return false;

        var position = 12;
        while (wave.Length - position >= 8)
        {
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wave.Slice(position + 4, 4));
            if (chunkSize < 0 || chunkSize > wave.Length - position - 8)
                return false;
            if (wave.Slice(position, 4).SequenceEqual("data"u8))
            {
                offset = position + 8;
                length = chunkSize & ~1;
                return true;
            }

            position += 8 + chunkSize + (chunkSize & 1);
        }

        return false;
    }

    /// <summary>Linear peak of the 16-bit samples, 0 (digital silence) to 1
    /// (full scale). Returns 0 when there is no data chunk.</summary>
    public static double PeakLevel(ReadOnlySpan<byte> wave)
    {
        if (!TryFindData(wave, out var offset, out var length))
            return 0;

        var peak = 0;
        var samples = wave.Slice(offset, length);
        for (var index = 0; index + 1 < samples.Length; index += 2)
        {
            int sample = BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(index, 2));
            var magnitude = sample < 0 ? -sample : sample;
            if (magnitude > peak) peak = magnitude;
        }

        return Math.Min(1.0, peak / 32768.0);
    }

    /// <summary>
    /// Copies <paramref name="wave"/> into a new pinned buffer with every
    /// 16-bit sample scaled by <paramref name="volume"/> (linear, clamped to
    /// 0..1). The buffer is allocated on the pinned object heap because the
    /// native async player reads it after this call returns; it must never
    /// move. Returns null when the buffer has no data chunk.
    /// </summary>
    public static byte[]? CreateScaledCopy(ReadOnlySpan<byte> wave, double volume)
    {
        if (!TryFindData(wave, out var offset, out var length))
            return null;

        var gain = double.IsFinite(volume) ? Math.Clamp(volume, 0.0, 1.0) : 0.0;
        var copy = GC.AllocateUninitializedArray<byte>(wave.Length, pinned: true);
        wave.CopyTo(copy);
        if (gain >= 1.0)
            return copy;

        var samples = copy.AsSpan(offset, length);
        for (var index = 0; index + 1 < samples.Length; index += 2)
        {
            var slot = samples.Slice(index, 2);
            var scaled = Math.Round(BinaryPrimitives.ReadInt16LittleEndian(slot) * gain, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(slot, (short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
        }

        return copy;
    }
}
