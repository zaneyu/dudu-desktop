using Dudu.App.Audio;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Audio;

public sealed class AudioCueServiceTests
{
    [Fact]
    public async Task DisposeAsync_disposes_an_optional_player_lifecycle_contract()
    {
        var player = new DisposableRecordingPlayer();
        await CreateService(player).DisposeAsync();

        Assert.True(player.Disposed);
    }
    [Fact]
    public async Task Maps_events_to_the_reviewed_pack_pairs()
    {
        var player = new RecordingPlayer();
        var now = new MutableClock();
        var service = CreateService(player, now: now, seed: 0);

        foreach (var (eventKind, expected) in new[]
        {
            (AudioCueEvent.Greeting, new[] { "tata-lala", "dudu-lalala" }),
            (AudioCueEvent.RemoteNote, new[] { "bubu-dudu-atata", "dudu-atatata" }),
            (AudioCueEvent.Reminder, new[] { "dudu-yapapa", "tata-lala" }),
            (AudioCueEvent.Celebration, new[] { "dudu-atatata", "bubu-dudu-atata" }),
            (AudioCueEvent.ManualInteraction, new[] { "dudu-lalala", "dudu-yapapa" }),
        })
        {
            var result = await PlayAsync(service, eventKind);
            Assert.Equal(AudioPlaybackStatus.Completed, result.Status);
            Assert.Contains(player.Played[^1].Split('/')[0], expected);
            now.Advance(AudioCueService.GlobalCooldown);
        }
    }

    [Fact]
    public async Task Selects_deterministic_cue_variants_within_a_pack()
    {
        var now = new MutableClock();
        var firstPlayer = new RecordingPlayer();
        var secondPlayer = new RecordingPlayer();
        var first = CreateService(firstPlayer, now: now, seed: 0, catalog: CreateCatalogWithVariants());
        var second = CreateService(secondPlayer, now: new MutableClock(), seed: 0, catalog: CreateCatalogWithVariants());

        Assert.Equal(AudioPlaybackStatus.Completed, (await PlayAsync(first, AudioCueEvent.Greeting)).Status);
        Assert.Equal(AudioPlaybackStatus.Completed, (await PlayAsync(second, AudioCueEvent.Greeting)).Status);
        Assert.Equal(firstPlayer.Played, secondPlayer.Played);
        Assert.Contains(firstPlayer.Played[0], new[] { "tata-lala/one.wav", "tata-lala/two.wav" });
    }

    [Fact]
    public async Task Failed_playback_does_not_consume_cooldown_or_variant_reservation()
    {
        var now = new MutableClock();
        var player = new RecordingPlayer { FailFirstCall = true };
        var service = CreateService(player, now: now, catalog: CreateCatalogWithVariants());

        Assert.Equal(AudioPlaybackStatus.Failed, (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
        Assert.Equal(AudioPlaybackStatus.Completed, (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
        Assert.Equal("tata-lala/one.wav", player.Played[1]);
    }

    [Fact]
    public async Task Cancelled_playback_does_not_consume_cooldown()
    {
        var player = new RecordingPlayer { BlockFirstCall = true };
        var service = CreateService(player);
        using var cancellation = new CancellationTokenSource();
        var first = service.TryPlayAsync(AudioCueEvent.Greeting, cancellation.Token);
        await player.FirstCallStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        Assert.Equal(AudioPlaybackStatus.Suppressed, (await first).Status);
        Assert.Equal(AudioPlaybackStatus.Completed, (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
    }

    [Fact]
    public async Task Seed_and_clock_make_variant_selection_deterministic()
    {
        var first = new RecordingPlayer();
        var second = new RecordingPlayer();
        var firstService = CreateService(first, seed: 17);
        var secondService = CreateService(second, seed: 17);

        await PlayAsync(firstService, AudioCueEvent.Greeting);
        await PlayAsync(secondService, AudioCueEvent.Greeting);

        Assert.Equal(first.Played, second.Played);
    }

    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(true, "quiet")]
    public async Task Suppresses_when_global_preferences_or_environment_suppress(bool soundsEnabled, string reason)
    {
        var player = new RecordingPlayer();
        var service = CreateService(player, preferences: Preferences.Default with { SoundsEnabled = soundsEnabled },
            isQuiet: reason == "quiet");

        var result = await PlayAsync(service, AudioCueEvent.Greeting);

        Assert.Equal(AudioPlaybackStatus.Suppressed, result.Status);
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task Suppresses_for_pause_fullscreen_lock_and_safe_mode()
    {
        foreach (var suppressed in new[]
        {
            new EnvironmentFlags { Paused = true },
            new EnvironmentFlags { Fullscreen = true },
            new EnvironmentFlags { Locked = true },
            new EnvironmentFlags { SafeMode = true },
        })
        {
            var player = new RecordingPlayer();
            var service = CreateService(player, flags: suppressed);

            Assert.Equal(AudioPlaybackStatus.Suppressed,
                (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
            Assert.Empty(player.Played);
        }
    }

    [Fact]
    public async Task Cancellation_before_reservation_is_suppressed()
    {
        var player = new RecordingPlayer();
        var service = CreateService(player);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.TryPlayAsync(AudioCueEvent.Greeting, cancellation.Token);

        Assert.Equal(AudioPlaybackStatus.Suppressed, result.Status);
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task Applies_global_and_per_pack_cooldowns()
    {
        var now = new MutableClock();
        var player = new RecordingPlayer();
        var service = CreateService(player, now: now);

        Assert.Equal(AudioPlaybackStatus.Completed,
            (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
        now.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(AudioPlaybackStatus.Completed,
            (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
        now.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(AudioPlaybackStatus.Suppressed,
            (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
        now.Advance(TimeSpan.FromMilliseconds(2000));
        Assert.Equal(AudioPlaybackStatus.Completed,
            (await PlayAsync(service, AudioCueEvent.Greeting)).Status);
    }

    [Fact]
    public async Task Does_not_overlap_calls_and_recovers_after_failure()
    {
        var player = new RecordingPlayer { BlockFirstCall = true, FailFirstCall = true };
        var service = CreateService(player);
        var first = service.TryPlayAsync(AudioCueEvent.Greeting, TestContext.Current.CancellationToken);
        await player.FirstCallStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var concurrent = await PlayAsync(service, AudioCueEvent.RemoteNote);
        Assert.Equal(AudioPlaybackStatus.Suppressed, concurrent.Status);

        player.ReleaseFirstCall();
        Assert.Equal(AudioPlaybackStatus.Failed, (await first).Status);

        var recovered = await PlayAsync(service, AudioCueEvent.RemoteNote);
        Assert.Equal(AudioPlaybackStatus.Completed, recovered.Status);
        Assert.Equal(2, player.CallCount);
    }

    private static AudioCueService CreateService(
        IAudioCuePlayer player,
        Preferences? preferences = null,
        MutableClock? now = null,
        int seed = 0,
        bool isQuiet = false,
        EnvironmentFlags? flags = null,
        AudioCatalog? catalog = null)
    {
        flags ??= new EnvironmentFlags();
        now ??= new MutableClock();
        return new AudioCueService(
            catalog ?? CreateCatalog(),
            player,
            () => preferences ?? Preferences.Default,
            () => isQuiet,
            () => flags.Paused,
            () => flags.Fullscreen,
            () => flags.Locked,
            () => flags.SafeMode,
            utcNow: () => now.Value,
            seed: seed);
    }

    private static Task<AudioPlaybackState> PlayAsync(AudioCueService service, AudioCueEvent cueEvent) =>
        service.TryPlayAsync(cueEvent, TestContext.Current.CancellationToken);

    private static AudioCatalog CreateCatalog() => new(new[]
    {
        Pack("bubu-dudu-atata"), Pack("tata-lala"), Pack("dudu-lalala"),
        Pack("dudu-atatata"), Pack("dudu-yapapa"),
    });

    private static AudioCatalog CreateCatalogWithVariants() => new(new[]
    {
        new AudioSoundPack("bubu-dudu-atata", new[] { new AudioCue("bubu-dudu-atata/one.wav", 100, "hash") }),
        new AudioSoundPack("tata-lala", new[]
        {
            new AudioCue("tata-lala/one.wav", 100, "hash"),
            new AudioCue("tata-lala/two.wav", 100, "hash"),
        }),
        new AudioSoundPack("dudu-lalala", new[] { new AudioCue("dudu-lalala/one.wav", 100, "hash") }),
        new AudioSoundPack("dudu-atatata", new[] { new AudioCue("dudu-atatata/one.wav", 100, "hash") }),
        new AudioSoundPack("dudu-yapapa", new[] { new AudioCue("dudu-yapapa/one.wav", 100, "hash") }),
    });

    private static AudioSoundPack Pack(string id) =>
        new(id, new[] { new AudioCue($"{id}/one.wav", 100, "hash") });

    private sealed class EnvironmentFlags
    {
        public bool Paused { get; init; }
        public bool Fullscreen { get; init; }
        public bool Locked { get; init; }
        public bool SafeMode { get; init; }
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Value { get; private set; } = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan amount) => Value += amount;
    }

    private sealed class RecordingPlayer : IAudioCuePlayer
    {
        public List<string> Played { get; } = [];
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool BlockFirstCall { get; init; }
        public bool FailFirstCall { get; init; }
        public TaskCompletionSource<bool> FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AudioPlaybackState> PlayAsync(AudioCue cue, double volume, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            Played.Add(cue.RelativeFile);
            if (call == 1)
            {
                FirstCallStarted.TrySetResult(true);
                if (BlockFirstCall) await _release.Task.WaitAsync(cancellationToken);
                if (FailFirstCall) return AudioPlaybackState.Failed;
            }
            return AudioPlaybackState.Completed;
        }

        public void ReleaseFirstCall() => _release.TrySetResult(true);
    }

    private sealed class DisposableRecordingPlayer : IAudioCuePlayer, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task<AudioPlaybackState> PlayAsync(
            AudioCue cue,
            double volume,
            CancellationToken cancellationToken) =>
            Task.FromResult(AudioPlaybackState.Completed);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
