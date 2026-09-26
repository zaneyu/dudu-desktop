using System.Buffers.Binary;

namespace Dudu.App.Tests.Audio;

internal static class TestWaves
{
    /// <summary>Mono 48 kHz 16-bit PCM WAV with the given samples. A LIST
    /// chunk (odd-sized, so it carries a pad byte) sits between fmt and data,
    /// the same layout as the shipped cues.</summary>
    public static byte[] Pcm16(params short[] samples)
    {
        var list = "LIST\u0003\0\0\0abc\0"u8.ToArray();
        var dataLength = samples.Length * 2;
        var bytes = new byte[12 + 24 + list.Length + 8 + dataLength];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 48_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 96_000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        list.CopyTo(bytes.AsSpan(36));
        var data = 36 + list.Length;
        "data"u8.CopyTo(bytes.AsSpan(data));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(data + 4), dataLength);
        for (var index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(data + 8 + index * 2), samples[index]);
        return bytes;
    }

    public static int DataOffset(byte[] wave) => 36 + 12 + 8;

    public static short[] ReadSamples(byte[] wave, int count)
    {
        var offset = DataOffset(wave);
        var samples = new short[count];
        for (var index = 0; index < count; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(offset + index * 2));
        return samples;
    }
}
