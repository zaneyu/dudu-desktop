using Dudu.App.Audio;
using Xunit;

namespace Dudu.App.Tests.Audio;

public sealed class WindowsAudioCuePlayerContractTests
{
    [Fact]
    public async Task Missing_asset_uses_the_no_op_fallback()
    {
        var player = new WindowsAudioCuePlayer(assetRoot: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var result = await player.PlayAsync(new AudioCue("tata-lala/one.wav", 100, "hash"), 0.5,
            TestContext.Current.CancellationToken);

        Assert.Equal(AudioPlaybackStatus.Completed, result.Status);
    }

    [Fact]
    public void Source_uses_local_media_player_and_does_not_use_sound_player_or_network_urls()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        Assert.Contains("MediaPlayer", source);
        Assert.Contains("player.Source = null", source);
        Assert.DoesNotContain("player.Pause", source);
        Assert.DoesNotContain("SoundPlayer", source);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Completion_wait_is_bounded_so_a_stalled_player_cannot_block_the_tick_loop()
    {
        // Regression: PlayAsync used to await completion.Task with no
        // timeout. That await sits on AudioCueService.TryPlayAsync, which
        // is called directly from the single 30-second reminder tick loop --
        // a MediaPlayer that never raises MediaEnded/MediaFailed (a hung
        // native decoder) would stall reminders, note delivery, and ambient
        // behaviour for the rest of the session.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        Assert.Contains("completion.Task.WaitAsync(", source);
        Assert.DoesNotContain("return await completion.Task.ConfigureAwait(false);", source);
        Assert.Contains("catch (TimeoutException)", source);
    }

    [Fact]
    public void Timeout_teardown_runs_off_the_calling_task_instead_of_inline()
    {
        // Finding 8: on the TimeoutException path, MediaEnded/MediaFailed
        // never fired -- the decoder may be hung -- so tearing the player
        // down (Source = null, Dispose) right there in `finally` can block
        // on the same hung native call, reintroducing the exact stall the
        // completion timeout exists to avoid (this call sits on the
        // 30-second reminder tick loop). The timeout path must defer
        // teardown to its own background Task.Run instead of running it on
        // this method's own continuation; the normal (non-timeout) path is
        // unaffected and still tears down inline.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        Assert.Contains("timedOut = true;", source);
        Assert.Contains("if (timedOut)", source);
        Assert.Contains("Task.Run(", source);
    }

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
