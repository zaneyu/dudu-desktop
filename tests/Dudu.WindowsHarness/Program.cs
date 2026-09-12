using System.Diagnostics;
using System.Runtime.InteropServices;
using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Models;

if (Array.IndexOf(args, "single-instance") >= 0)
{
    return await RunSingleInstanceScenarioAsync(args);
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
    openHome: () => Console.WriteLine("OpenHome callback"),
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
