using System.Runtime.InteropServices;
using Dudu.App.Audio;
using Xunit;

namespace Dudu.App.Tests.Audio;

public sealed class WindowsAudioCuePlayerContractTests
{
    [Fact]
    public async Task Plays_the_volume_scaled_in_memory_wave_asynchronously()
    {
        var calls = new List<(short[] Samples, uint Flags)>();
        var player = new WindowsAudioCuePlayer((sound, flags) =>
        {
            // Read the buffer during the call, the way winmm starts reading it.
            var copy = new byte[TestWaves.DataOffset(TestWaves.Pcm16(0)) + 4];
            Marshal.Copy(sound, copy, 0, copy.Length);
            calls.Add((TestWaves.ReadSamples(copy, 2), flags));
            return true;
        });

        var result = await player.PlayAsync(Cue(TestWaves.Pcm16(1000, -2000)), 0.5,
            TestContext.Current.CancellationToken);

        Assert.Equal(AudioPlaybackStatus.Started, result.Status);
        var call = Assert.Single(calls);
        Assert.Equal(new short[] { 500, -1000 }, call.Samples);
        Assert.Equal(WindowsAudioCuePlayer.PlayFlags, call.Flags);
        Assert.Equal(0x0007u, WindowsAudioCuePlayer.PlayFlags); // SND_ASYNC | SND_NODEFAULT | SND_MEMORY
    }

    [Fact]
    public async Task A_cue_without_wave_bytes_fails_without_a_native_call()
    {
        var called = false;
        var player = new WindowsAudioCuePlayer((_, _) => called = true);

        var result = await player.PlayAsync(new AudioCue("tata-lala/one.wav", 100, "hash"), 0.5,
            TestContext.Current.CancellationToken);

        Assert.Equal(AudioPlaybackStatus.Failed, result.Status);
        Assert.False(called);
    }

    [Fact]
    public async Task A_rejected_native_call_is_failed()
    {
        var player = new WindowsAudioCuePlayer((_, _) => false);

        var result = await player.PlayAsync(Cue(TestWaves.Pcm16(1000)), 0.5,
            TestContext.Current.CancellationToken);

        Assert.Equal(AudioPlaybackStatus.Failed, result.Status);
    }

    [Fact]
    public async Task A_hung_native_call_is_bounded_and_the_next_call_fails_fast()
    {
        // Regression guard for the tick loop: a wedged audio driver must not
        // stall the 30 s reminder tick or an explicit pet action.
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var player = new WindowsAudioCuePlayer((_, _) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(30));
            return true;
        });

        var started = DateTime.UtcNow;
        var first = await player.PlayAsync(Cue(TestWaves.Pcm16(1000)), 0.5,
            TestContext.Current.CancellationToken);
        var second = await player.PlayAsync(Cue(TestWaves.Pcm16(1000)), 0.5,
            TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - started;
        release.Set();

        Assert.Equal(AudioPlaybackStatus.Failed, first.Status);
        Assert.Equal(AudioPlaybackStatus.Failed, second.Status);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(elapsed < WindowsAudioCuePlayer.NativeCallTimeout + TimeSpan.FromSeconds(3), $"took {elapsed}");
    }

    [Fact]
    public async Task Cancellation_and_disposal_suppress_without_a_native_call()
    {
        var calls = 0;
        var player = new WindowsAudioCuePlayer((_, _) => { calls++; return true; });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(AudioPlaybackStatus.Suppressed,
            (await player.PlayAsync(Cue(TestWaves.Pcm16(1000)), 0.5, cancellation.Token)).Status);
        await player.DisposeAsync();
        Assert.Equal(AudioPlaybackStatus.Suppressed,
            (await player.PlayAsync(Cue(TestWaves.Pcm16(1000)), 0.5, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Dispose_stops_a_playing_sound()
    {
        var calls = new List<(nint Sound, uint Flags)>();
        var player = new WindowsAudioCuePlayer((sound, flags) =>
        {
            lock (calls) calls.Add((sound, flags));
            return true;
        });
        await player.PlayAsync(Cue(TestWaves.Pcm16(1000)), 0.5, TestContext.Current.CancellationToken);

        await player.DisposeAsync();

        Assert.Equal(2, calls.Count);
        Assert.Equal((nint)0, calls[1].Sound);
        Assert.Equal(0u, calls[1].Flags);
    }

    [Fact]
    public void Source_uses_in_memory_play_sound_not_media_player_files_or_network_urls()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        Assert.Contains("\"winmm.dll\"", source);
        Assert.Contains("DllImportSearchPath.System32", source);
        Assert.DoesNotContain("new MediaPlayer", source);
        Assert.DoesNotContain("SoundPlayer", source);
        Assert.DoesNotContain("new Uri(", source);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
    }

    private static AudioCue Cue(byte[] wave) =>
        new("tata-lala/one.wav", 100, "hash") { WaveData = wave, PeakLevel = WavPcm.PeakLevel(wave) };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }
}
