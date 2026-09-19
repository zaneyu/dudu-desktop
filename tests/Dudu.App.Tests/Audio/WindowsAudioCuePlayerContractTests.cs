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
