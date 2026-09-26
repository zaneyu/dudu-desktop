using Dudu.App.Audio;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Audio;

/// <summary>Regression coverage for cues at zero volume and for cues showing
/// up as a Windows media session.</summary>
public sealed class AudioMuteUxTests
{
    [Fact]
    public async Task Volume_at_zero_is_muted_and_does_not_eat_the_next_cue()
    {
        var player = new CountingPlayer();
        var preferences = Preferences.Default with { SoundsEnabled = true, SoundVolume = 0 };
        var now = DateTimeOffset.Parse("2026-09-12T10:00:00Z");
        var service = new AudioCueService(Catalog(), player, () => preferences, utcNow: () => now);

        var muted = await service.TryPlayAsync(AudioCueEvent.Greeting, TestContext.Current.CancellationToken);

        Assert.Equal(AudioPlaybackStatus.Suppressed, muted.Status);
        Assert.Equal(0, player.Calls);

        // Turning the volume back up plays straight away: the muted attempt
        // must not have started a cooldown window.
        preferences = preferences with { SoundVolume = 0.5 };
        var audible = await service.TryPlayAsync(AudioCueEvent.Greeting, TestContext.Current.CancellationToken);

        Assert.Equal(AudioPlaybackStatus.Completed, audible.Status);
        Assert.Equal(1, player.Calls);
    }

    [Fact]
    public void Windows_cue_player_plays_sound_effects_without_a_media_session()
    {
        // winmm PlaySound never registers a system media session, so a cue
        // cannot surface in the volume flyout's media controls the way a
        // MediaPlayer would.
        var source = ReadRepositoryFile("src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs");

        Assert.Contains("PlaySound", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new MediaPlayer", source, StringComparison.Ordinal);
    }

    private static AudioCatalog Catalog() => new(new[]
    {
        Pack("bubu-dudu-atata"), Pack("tata-lala"), Pack("dudu-lalala"),
        Pack("dudu-atatata"), Pack("dudu-yapapa"),
    });

    private static AudioSoundPack Pack(string id) =>
        new(id, new[] { new AudioCue($"{id}/one.wav", 100, "hash") });

    private sealed class CountingPlayer : IAudioCuePlayer
    {
        public int Calls { get; private set; }

        public Task<AudioPlaybackState> PlayAsync(AudioCue cue, double volume, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AudioPlaybackState.Completed);
        }
    }

    private static string ReadRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
