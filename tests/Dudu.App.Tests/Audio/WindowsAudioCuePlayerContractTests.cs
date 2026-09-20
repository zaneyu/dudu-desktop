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

    [Fact]
    public void Timeout_teardown_also_defers_the_media_event_unsubscribe()
    {
        // Finding 15: MediaEnded/MediaFailed unsubscribe used to still run
        // inline in `finally` even on the timeout branch, against a
        // possibly-hung player -- the same class of stall the deferred
        // Source/Dispose teardown above already exists to avoid. Both
        // unsubscribes must live inside the same background Task.Run as
        // the rest of the timeout-path teardown, not before the
        // `if (timedOut)` branch.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        var finallyIndex = source.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0, "Expected a finally block.");
        var timedOutBranchIndex = source.IndexOf("if (timedOut)", finallyIndex, StringComparison.Ordinal);
        Assert.True(timedOutBranchIndex >= 0, "Expected the timedOut branch inside finally.");

        // Nothing between `finally` and the timedOut branch touches the
        // MediaPlayer's event subscriptions -- both unsubscribe calls must
        // be inside a conditional branch, never unconditional ahead of it.
        var beforeBranch = source[finallyIndex..timedOutBranchIndex];
        Assert.DoesNotContain("MediaEnded -=", beforeBranch);
        Assert.DoesNotContain("MediaFailed -=", beforeBranch);

        var taskRunIndex = source.IndexOf("Task.Run(", timedOutBranchIndex, StringComparison.Ordinal);
        Assert.True(taskRunIndex >= 0, "Expected the deferred Task.Run inside the timedOut branch.");
        var elseIndex = source.IndexOf("else", taskRunIndex, StringComparison.Ordinal);
        Assert.True(elseIndex >= 0, "Expected the non-timeout else branch after Task.Run.");

        // Both unsubscribes appear inside the deferred task (between
        // Task.Run( and the else branch that handles the normal path).
        var deferredBody = source[taskRunIndex..elseIndex];
        Assert.Contains("hungPlayer.MediaEnded -= ended;", deferredBody);
        Assert.Contains("hungPlayer.MediaFailed -= failed;", deferredBody);

        // The normal (non-timeout) path still tears the subscriptions down
        // inline, synchronously with the rest of its teardown.
        var normalBody = source[elseIndex..];
        Assert.Contains("player.MediaEnded -= ended;", normalBody);
        Assert.Contains("player.MediaFailed -= failed;", normalBody);
    }

    [Fact]
    public void Cancellation_registration_only_completes_the_tcs_and_never_touches_the_player()
    {
        // Finding H: the `using var cancellation` registration below is
        // disposed on every exit path, and CancellationTokenRegistration
        // .Dispose() blocks until a currently-running callback finishes. If
        // that callback touched `player` (e.g. Source = null) while a hung
        // native decoder still owned it, disposing the registration -- and
        // therefore PlayAsync itself -- could still stall despite the
        // completion timeout that exists specifically to prevent that. The
        // callback must only ever complete the completion source; all player
        // teardown stays in `finally` or the deferred Task.Run.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        var registrationStart = source.IndexOf("cancellationToken.Register(", StringComparison.Ordinal);
        Assert.True(registrationStart >= 0, "Expected a cancellationToken.Register call.");
        var registrationEnd = source.IndexOf("});", registrationStart, StringComparison.Ordinal);
        Assert.True(registrationEnd >= 0, "Expected the registration callback to close with `});`.");
        var callbackBody = source[registrationStart..registrationEnd];

        Assert.DoesNotContain("player.Source", callbackBody);
        Assert.Contains("completion.TrySetResult(AudioPlaybackState.Suppressed);", callbackBody);
    }

    [Fact]
    public void Normal_path_teardown_guards_dispose_so_it_cannot_throw_out_of_the_finally_block()
    {
        // Finding H: the timeout path's deferred teardown already guards
        // both Source = null and Dispose() in their own try/catch; the
        // normal (non-timeout) path guarded only the former, leaving
        // Dispose() free to throw out of this method's finally block.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Audio", "WindowsAudioCuePlayer.cs"));

        Assert.Contains("try { player.Dispose(); } catch { }", source);
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
