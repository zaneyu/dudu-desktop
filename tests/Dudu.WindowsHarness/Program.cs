using System.Diagnostics;
using System.Runtime.InteropServices;
using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.Overlay;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Remote;
using Microsoft.Extensions.DependencyInjection;

if (Array.IndexOf(args, "single-instance") >= 0)
{
    return await RunSingleInstanceScenarioAsync(args);
}

if (Array.IndexOf(args, "notifications") >= 0)
{
    return await RunNotificationsScenarioAsync();
}

if (Array.IndexOf(args, "remote-note") >= 0)
{
    return await RunRemoteNoteScenarioAsync();
}

if (Array.IndexOf(args, "outage-reminder") >= 0)
{
    return await RunOutageReminderScenarioAsync(args);
}

if (!args.Contains("--scenario", StringComparer.Ordinal)
    || Array.IndexOf(args, "layered-window") < 0)
{
    Console.Error.WriteLine("Manual harness only. Run with --scenario layered-window on Windows.");
    return 2;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The layered-window harness requires Windows x64 and is intentionally manual.");
    return 3;
}

var manifestPath = args.Length > 1 && File.Exists(args[1])
    ? args[1]
    : Path.Combine(
        AppContext.BaseDirectory,
        "src",
        "Dudu.App",
        "Assets",
        "Packs",
        "fallback",
        "manifest.json");

if (!File.Exists(manifestPath))
{
    manifestPath = Path.Combine(
        Environment.CurrentDirectory,
        "src",
        "Dudu.App",
        "Assets",
        "Packs",
        "fallback",
        "manifest.json");
}

if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"Manifest not found: {manifestPath}");
    return 4;
}

using var notepad = await FindOrLaunchNotepadAsync();
if (notepad is null || notepad.MainWindowHandle == IntPtr.Zero)
{
    Console.Error.WriteLine("Could not identify a Notepad window. The harness cannot run the cross-process pass-through check.");
    return 5;
}

var foregroundBefore = OverlayWindowHost.GetForegroundWindowHandle();
var focusBefore = OverlayWindowHost.GetFocusHandle();
Console.WriteLine($"Notepad HWND: 0x{notepad.MainWindowHandle.ToInt64():X}");
Console.WriteLine($"Foreground before overlay: 0x{foregroundBefore:X}");
Console.WriteLine($"Focus before overlay:      0x{focusBefore:X}");

var pack = await AssetManifestLoader.LoadAsync(manifestPath, CancellationToken.None);
using var composer = new SkiaFrameComposer(pack);
var animation = pack.Manifest.Outfits["base"].Animations["idle"];
using var presenter = new LayeredFramePresenter();
using var host = await OverlayWindowHost.CreateAsync(
    presenter,
    new PetPlacement("MISSING", 0.8, 0.8, 1),
    animation.NominalSize,
    openHome: _ =>
    {
        Console.WriteLine("OpenHome callback");
        return Task.CompletedTask;
    },
    showContextMenu: () => Console.WriteLine("Context menu callback"));

using var frame = composer.Compose(pack, animation, 0);
await presenter.PresentAsync(frame, CancellationToken.None);
host.Show();
await Task.Delay(250);

var foregroundAfterShow = OverlayWindowHost.GetForegroundWindowHandle();
var focusAfterShow = OverlayWindowHost.GetFocusHandle();
var cornerInteractive = presenter.IsInteractiveAt(1, 1);
var centerInteractive = presenter.IsInteractiveAt(frame.Width / 2, frame.Height / 2);
Console.WriteLine($"Foreground after overlay:  0x{foregroundAfterShow:X}");
Console.WriteLine($"Focus after overlay:        0x{focusAfterShow:X}");
Console.WriteLine($"Frame: {frame.Width}x{frame.Height}; host HWND: 0x{host.Handle:X}");
Console.WriteLine($"Programmatic alpha seam: corner interactive={cornerInteractive}; center interactive={centerInteractive}");
Console.WriteLine();
Console.WriteLine("Manual checks (the harness does not claim runtime proof on non-Windows hosts):");
Console.WriteLine("1. Click a transparent corner over Notepad: Notepad must receive the click/focus; overlay must not activate.");
Console.WriteLine("2. Click a visible pixel: overlay must receive the click without changing the foreground application.");
Console.WriteLine("3. Drag from a visible pixel across the monitor; release. The pet must stay at the dropped position after a display/DPI refresh.");
Console.WriteLine("4. Hover the pet and wheel up/down. Scale must change only within 0.5x..2x and the window must remain visible.");
Console.WriteLine("5. Double-click the visible pet for OpenHome and right-click it for Context menu; neither may activate the overlay.");
Console.WriteLine();
Console.Write("Enter PASS or FAIL (include a short reason for FAIL): ");
var verdict = Console.ReadLine()?.Trim();
Console.WriteLine($"Harness result: {verdict ?? "NO-VERDICT"}");
host.Hide();

if (notepad.StartedByHarness)
{
    try
    {
        if (!notepad.Process.CloseMainWindow())
        {
            notepad.Process.Kill(entireProcessTree: true);
        }
    }
    catch (InvalidOperationException)
    {
        // Notepad may have exited during manual testing.
    }
}

return string.Equals(verdict, "PASS", StringComparison.OrdinalIgnoreCase) ? 0 : 6;

static async Task<int> RunSingleInstanceScenarioAsync(string[] args)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("The single-instance harness requires Windows x64 and is intentionally manual.");
        return 3;
    }

    var marker = args
        .SkipWhile(argument => !string.Equals(argument, "--marker", StringComparison.Ordinal))
        .Skip(1)
        .FirstOrDefault();
    if (string.IsNullOrWhiteSpace(marker))
    {
        Console.Error.WriteLine("--marker is required for the single-instance harness.");
        return 4;
    }

    if (args.Contains("--child", StringComparer.Ordinal))
    {
        await using var coordinator = new SingleInstanceCoordinator(
            activationHandler: activation =>
            {
                if (activation == AppActivation.OpenHome)
                {
                    File.AppendAllText(marker, "OpenHome\n");
                }

                return Task.CompletedTask;
            });
        var isPrimary = await coordinator.TryAcquireAsync();
        Console.WriteLine(isPrimary ? "primary" : "secondary");
        if (isPrimary)
        {
            var manifestPath = Path.Combine(
                Environment.CurrentDirectory,
                "src",
                "Dudu.App",
                "Assets",
                "Packs",
                "fallback",
                "manifest.json");
            var pack = await AssetManifestLoader.LoadAsync(manifestPath, CancellationToken.None);
            using var composer = new SkiaFrameComposer(pack);
            using var presenter = new LayeredFramePresenter();
            var animation = pack.Manifest.Outfits["base"].Animations["idle"];
            using var host = await OverlayWindowHost.CreateAsync(
                presenter,
                new PetPlacement("MISSING", 0.8, 0.8, 1),
                animation.NominalSize);
            using var frame = composer.Compose(pack, animation, 0);
            await presenter.PresentAsync(frame, CancellationToken.None);
            host.Show();
            await Task.Delay(TimeSpan.FromSeconds(4));
            host.Hide();
        }

        return 0;
    }

    DeleteIfPresent(marker);
    DeleteIfPresent(marker + ".overlay");
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Harness executable path is unavailable.");
    using var primaryProcess = Process.Start(new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        ArgumentList = { "--scenario", "single-instance", "--child", "--marker", marker },
    });
    if (primaryProcess is null) return 5;
    await Task.Delay(300);
    using var secondaryProcess = Process.Start(new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        ArgumentList = { "--scenario", "single-instance", "--child", "--marker", marker },
    });
    if (secondaryProcess is null) return 6;
    await secondaryProcess.WaitForExitAsync();
    var observedWindows = await WaitForDuduWindowsAsync(primaryProcess.Id);
    await primaryProcess.WaitForExitAsync();

    var activations = File.Exists(marker)
        ? File.ReadAllLines(marker).Count(line => line == "OpenHome")
        : 0;
    Console.WriteLine($"primaryExit={primaryProcess.ExitCode}; secondaryExit={secondaryProcess.ExitCode}; OpenHome={activations}; DuduWindows={observedWindows}");
    return primaryProcess.ExitCode == 0 && secondaryProcess.ExitCode == 0 && activations == 1 && observedWindows == 1 ? 0 : 7;
}

static async Task<int> RunNotificationsScenarioAsync()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("The notifications harness requires Windows x64 and is intentionally manual.");
        return 3;
    }

    var notifications = new AppNotificationService(new WindowsAppNotificationSink());
    var registered = await notifications.TryRegisterAsync(CancellationToken.None);
    Console.WriteLine($"Notification registration: {(registered ? "succeeded" : "failed")}");

    var messageId = Guid.NewGuid();
    await notifications.ShowRemoteNoteArrivalAsync(messageId, CancellationToken.None);
    Console.WriteLine($"Showed remote-note toast for message {messageId:D}.");

    var pet = PetStateMachine.CreateIdle();
    var presentation = pet.Handle(new PetEvent.RemoteNoteArrived(messageId.ToString("D")));
    Console.WriteLine($"Pet bubble fallback state: {presentation.State}; title: {presentation.BubbleTitle}");

    Console.WriteLine();
    Console.WriteLine("Manual checks:");
    Console.WriteLine("1. A Windows toast titled 'A note arrived 💌' appears with no body text, no image, and no preview of any message content.");
    Console.WriteLine("2. Disable notifications for this app (Windows Settings > Notifications), rerun this scenario, and confirm the pet bubble above still shows the same generic text with no data loss.");
    Console.WriteLine();
    Console.Write("Enter PASS or FAIL (include a short reason for FAIL): ");
    var verdict = Console.ReadLine()?.Trim();
    Console.WriteLine($"Harness result: {verdict ?? "NO-VERDICT"}");
    return string.Equals(verdict, "PASS", StringComparison.OrdinalIgnoreCase) ? 0 : 6;
}

static async Task<int> RunRemoteNoteScenarioAsync()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("The remote-note harness requires Windows x64 (DPAPI secret storage) and is intentionally manual.");
        return 3;
    }

    var baseUrl = Environment.GetEnvironmentVariable("DUDU_RELAY_BASE_URL");
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        Console.Error.WriteLine(
            "DUDU_RELAY_BASE_URL is required. Start `cd relay && npx wrangler dev --local --port 8787` " +
            "and set DUDU_RELAY_BASE_URL to its address before running this scenario.");
        return 4;
    }

    var workingDirectory = Path.Combine(
        Path.GetTempPath(), "dudu-harness-remote-note-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workingDirectory);
    var databasePath = Path.Combine(workingDirectory, "dudu.db");
    var backupDirectory = Path.Combine(workingDirectory, "backups");

    var notifications = new AppNotificationService(new WindowsAppNotificationSink());
    await notifications.TryRegisterAsync(CancellationToken.None);
    var noteArrived = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

    var services = new ServiceCollection()
        .AddDuduInfrastructure(
            new DatabaseOptions(databasePath, backupDirectory),
            new RelayOptions(new Uri(baseUrl)))
        .AddSingleton<IRemoteNoteArrivalSink>(new HarnessRemoteNoteArrivalSink(notifications, noteArrived))
        .BuildServiceProvider();

    await using var sync = services.GetRequiredService<RemoteSyncService>();
    try
    {
        var database = services.GetRequiredService<Database>();
        await database.InitializeAsync(CancellationToken.None);

        var pairingCode = await sync.CreatePairingCodeAsync(CancellationToken.None);
        if (pairingCode.Availability != PairingAvailability.Available || pairingCode.Code is null)
        {
            Console.Error.WriteLine($"Pairing code creation failed; availability={pairingCode.Availability}.");
            return 5;
        }

        Console.WriteLine($"Pairing code: {pairingCode.Code} (expires {pairingCode.ExpiresUtc:O})");
        Console.WriteLine("Open the sender page on a phone, enter the code, and send an encrypted fixture note now.");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!noteArrived.Task.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await sync.PollOnceAsync(CancellationToken.None);
            if (!noteArrived.Task.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        if (!noteArrived.Task.IsCompleted)
        {
            Console.Error.WriteLine("No note arrived within 30 seconds.");
            return 6;
        }

        var messageId = await noteArrived.Task;
        var revealed = await sync.RevealAsync(messageId.ToString("D"), CancellationToken.None);
        Console.WriteLine($"Revealed text: \"{revealed.Text}\"; reaction: {revealed.Reaction}");

        Console.WriteLine();
        Console.WriteLine("Manual checks (the harness cannot assert these on its own):");
        Console.WriteLine("1. The Windows toast that appeared above said only 'A note arrived 💌' with no note text or reaction.");
        Console.WriteLine("2. Query the relay's local D1 database (e.g. via `wrangler d1 execute`) and confirm the stored row for this message id never contains the plaintext printed above.");
        Console.WriteLine("3. Confirm the relay has no leftover ciphertext for this message id after this run's acknowledgment.");
        Console.WriteLine();
        Console.Write("Enter PASS or FAIL (include a short reason for FAIL): ");
        var verdict = Console.ReadLine()?.Trim();
        Console.WriteLine($"Harness result: {verdict ?? "NO-VERDICT"}");
        return string.Equals(verdict, "PASS", StringComparison.OrdinalIgnoreCase) ? 0 : 7;
    }
    finally
    {
        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; leftover temp files here carry no plaintext or secrets.
        }
    }
}

/// <summary>
/// Ruling #3 (task-21-review-1.md, Important) requires the harness itself to prove a due local
/// reminder still fires end-to-end after the relay worker has gone dark, through the normal
/// reminder path rather than a substitute. This scenario schedules a reminder due in
/// <c>--due-reminder-seconds N</c> seconds (default 5) via the real <see cref="IReminderWriter"/>,
/// starts the real <see cref="AppHost"/> wired to a real <see cref="Dudu.Core.Reminders.ReminderEngine"/>
/// (resolved through the same <c>AddDuduInfrastructure</c> DI registration production uses) and a
/// real, non-UI <see cref="PresentationCoordinator"/> (the minimal construction already exercised by
/// <c>PresentationCoordinatorTests</c>) wired in as the reminder due sink's gateway. No relay base
/// URL is configured and no <see cref="RemoteSyncService"/> is attached, so this exercises only the
/// local reminder path -- exactly what must survive a relay/worker outage.
///
/// Rather than waiting out the real 30-second periodic scheduler, this calls
/// <see cref="AppHost.ResumeAsync"/> once the reminder is due -- the same on-demand immediate-tick
/// path <c>AppHostTests</c> already drives via <c>ResumeAsync</c> to avoid wall-clock waits, not a
/// new or invented scheduler. When the coordinator actually presents the reminder (and only then),
/// this prints a single sentinel line, <c>REMINDER-PRESENTED &lt;id&gt;</c>, to stdout.
/// </summary>
static async Task<int> RunOutageReminderScenarioAsync(string[] args)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("The outage-reminder harness requires Windows x64 and is intentionally manual.");
        return 3;
    }

    var dueSeconds = 5;
    var flagIndex = Array.IndexOf(args, "--due-reminder-seconds");
    if (flagIndex >= 0
        && flagIndex + 1 < args.Length
        && int.TryParse(args[flagIndex + 1], out var parsedSeconds)
        && parsedSeconds > 0)
    {
        dueSeconds = parsedSeconds;
    }

    var workingDirectory = Path.Combine(
        Path.GetTempPath(), "dudu-harness-outage-reminder-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workingDirectory);
    var databasePath = Path.Combine(workingDirectory, "dudu.db");
    var backupDirectory = Path.Combine(workingDirectory, "backups");

    var reminderId = Guid.NewGuid().ToString("D");
    const string reminderTitle = "Outage harness reminder";

    PresentationCoordinator? coordinator = null;
    var services = new ServiceCollection()
        .AddDuduInfrastructure(new DatabaseOptions(databasePath, backupDirectory))
        .AddSingleton<IReminderDueSink>(provider => new ReminderDueSink(
            provider.GetRequiredService<IReminderRepository>(),
            () => coordinator
                ?? throw new InvalidOperationException("Presentation coordinator is not composed yet.")))
        .BuildServiceProvider();

    var innerNotifications = new AppNotificationService(new WindowsAppNotificationSink());
    await innerNotifications.TryRegisterAsync(CancellationToken.None);
    var notifications = new SentinelReminderNotificationService(innerNotifications);
    var pet = PetStateMachine.CreateIdle();
    coordinator = new PresentationCoordinator(
        new PresentationPolicy(TimeSpan.Zero),
        notifications,
        pet,
        (_, _, _) => Task.CompletedTask,
        () => AnimationOptions.Default,
        isQuietHours: () => false,
        pauseState: () => PauseState.None,
        petGate: new SemaphoreSlim(1, 1));

    var appHost = new AppHost(services, AppPaths.ForRoot(workingDirectory));
    appHost.AttachPresentationGateway(coordinator);

    try
    {
        var writer = services.GetRequiredService<IReminderWriter>();
        var nextDueUtc = DateTimeOffset.UtcNow.AddSeconds(dueSeconds);
        await writer.SaveAsync(
            new Reminder(
                reminderId,
                reminderTitle,
                Details: null,
                Enabled: true,
                Rule: new RecurrenceRule.Once(),
                LocalTimeZoneId: TimeZoneInfo.Local.Id,
                QuietHoursBehavior: QuietHoursBehavior.DeliverImmediately,
                MissedPolicy: MissedOccurrencePolicy.Skip,
                NextDueUtc: nextDueUtc),
            CancellationToken.None);

        Console.WriteLine($"Scheduled reminder {reminderId} due at {nextDueUtc:O} ({dueSeconds}s from now); no relay base URL configured.");

        await appHost.StartAsync(CancellationToken.None);

        var remaining = nextDueUtc - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromMilliseconds(200));
        }

        await appHost.ResumeAsync(CancellationToken.None);

        if (notifications.PresentedReminderId is null)
        {
            Console.Error.WriteLine("The scheduled reminder was not presented after becoming due.");
            return 6;
        }

        Console.WriteLine($"REMINDER-PRESENTED {notifications.PresentedReminderId}");
        return 0;
    }
    finally
    {
        await appHost.StopAsync(CancellationToken.None);
        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; leftover temp files here carry no plaintext or secrets.
        }
    }
}

static void DeleteIfPresent(string path)
{
    if (File.Exists(path)) File.Delete(path);
}

static async Task<int> WaitForDuduWindowsAsync(int processId)
{
    for (var attempt = 0; attempt < 40; attempt++)
    {
        var count = CountDuduWindows(processId);
        if (count > 0) return count;
        await Task.Delay(50);
    }

    return CountDuduWindows(processId);
}

static int CountDuduWindows(int processId)
{
    var count = 0;
    NativeMethods.EnumWindows((window, _) =>
    {
        NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId);
        if (ownerProcessId != processId || !NativeMethods.IsWindowVisible(window)) return true;
        Span<char> className = stackalloc char[256];
        var length = NativeMethods.GetClassName(window, ref MemoryMarshal.GetReference(className), className.Length);
        if (length == OverlayWindowHost.WindowClassName.Length
            && className[..length].SequenceEqual(OverlayWindowHost.WindowClassName))
        {
            count++;
        }

        return true;
    }, 0);
    return count;
}

static async Task<NotepadTarget?> FindOrLaunchNotepadAsync()
{
    var existing = Process.GetProcessesByName("notepad")
        .FirstOrDefault(process =>
        {
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        });
    if (existing is not null)
    {
        return new NotepadTarget(existing, false);
    }

    var started = Process.Start(new ProcessStartInfo("notepad.exe")
    {
        UseShellExecute = true,
    });
    if (started is null)
    {
        return null;
    }

    for (var attempt = 0; attempt < 50; attempt++)
    {
        await Task.Delay(100);
        started.Refresh();
        if (started.HasExited)
        {
            return null;
        }

        if (started.MainWindowHandle != IntPtr.Zero)
        {
            return new NotepadTarget(started, true);
        }
    }

    started.Dispose();
    return null;
}

/// <summary>
/// Harness stand-in for <c>Dudu.App.Presentation.RemoteNoteArrivalSink</c>: shows the same
/// generic Windows toast the production sink would (via the same notification service), and
/// signals the scenario's poll loop without ever surfacing note text or reaction.
/// </summary>
file sealed class HarnessRemoteNoteArrivalSink(
    AppNotificationService notifications,
    TaskCompletionSource<Guid> arrived) : IRemoteNoteArrivalSink
{
    public async Task NotifyAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await notifications.ShowRemoteNoteArrivalAsync(messageId, cancellationToken);
        arrived.TrySetResult(messageId);
    }
}

/// <summary>
/// Wraps the real <see cref="AppNotificationService"/> so the outage-reminder scenario can
/// observe, from outside <see cref="PresentationCoordinator"/>, the exact moment it actually
/// presents a reminder (<see cref="ShowReminderAsync"/> is the same call
/// <c>PresentationCoordinator.ShowNotificationAsync</c> makes for a
/// <see cref="Dudu.App.Presentation.PresentationItemKind.Reminder"/> item) -- this is the hook
/// point ruling #3 calls for, not a bypass of the coordinator.
/// </summary>
file sealed class SentinelReminderNotificationService(AppNotificationService inner) : INotificationService
{
    public string? PresentedReminderId { get; private set; }

    public async Task ShowReminderAsync(string reminderId, string title, CancellationToken cancellationToken)
    {
        await inner.ShowReminderAsync(reminderId, title, cancellationToken);
        PresentedReminderId = reminderId;
    }

    public Task ShowRemoteNoteArrivalAsync(Guid messageId, CancellationToken cancellationToken) =>
        inner.ShowRemoteNoteArrivalAsync(messageId, cancellationToken);
}

file sealed class NotepadTarget(Process process, bool startedByHarness) : IDisposable
{
    public Process Process { get; } = process;

    public bool StartedByHarness { get; } = startedByHarness;

    public IntPtr MainWindowHandle => Process.MainWindowHandle;

    public void Dispose() => Process.Dispose();
}

file static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint window, ref char className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint window);

    public delegate bool EnumWindowsCallback(nint window, nint lParam);
}
