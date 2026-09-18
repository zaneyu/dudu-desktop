using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace Dudu.App.System;

public interface IStartupLinkWriter
{
    Task WriteAtomicAsync(
        string shortcutPath,
        string targetPath,
        string arguments,
        CancellationToken cancellationToken);

    Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken);
}

internal interface IPackagedStartupTaskRegistration
{
    Task<bool> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

public sealed class StartupRegistrationService : IAsyncDisposable
{
    public const string ShortcutFileName = "Dudu Desktop Companion.lnk";
    public const string PackagedStartupTaskId = "DuduDesktopStartupTask";

    private readonly IStartupLinkWriter _writer;
    private readonly IPackagedStartupTaskRegistration? _packagedStartupTask;
    private readonly string _installedExecutable;
    private readonly string _shortcutPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly bool _available;
    private readonly string? _initializationError;
    private bool _enabled;
    private bool _disposed;
    private Task? _disposeTask;

    public StartupRegistrationService(
        string? installedExecutable = null,
        string? startupDirectory = null,
        IStartupLinkWriter? writer = null)
        : this(installedExecutable, startupDirectory, writer, packagedStartupTask: null)
    {
    }

    internal StartupRegistrationService(
        string? installedExecutable,
        string? startupDirectory,
        IStartupLinkWriter? writer,
        IPackagedStartupTaskRegistration? packagedStartupTask)
    {
        _packagedStartupTask = packagedStartupTask ?? WindowsStartupTaskRegistration.TryCreate();
        string? executable = null;
        string? shortcut = null;
        string? error = null;
        var available = false;
        try
        {
            var startup = startupDirectory
                ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startup))
            {
                throw new InvalidOperationException("The current-user Startup folder is unavailable.");
            }

            // This is the only current-user Startup file owned by Dudu. Keep
            // the path fixed so packaged migration never deletes another app's
            // startup entry.
            shortcut = Path.GetFullPath(Path.Combine(startup, ShortcutFileName));
            if (_packagedStartupTask is null)
            {
                executable = Path.GetFullPath(
                    installedExecutable
                    ?? Environment.ProcessPath
                    ?? throw new InvalidOperationException("The installed executable path is unavailable."));
            }

            available = true;
        }
        catch (Exception exception)
        {
            // Lazy-degrade: never fail construction (and thus bootstrap).
            // The service reports disabled; writes throw a degraded error
            // that StartupSettingsService turns into NeedsReconciliation.
            error = exception.Message;
        }

        _installedExecutable = executable ?? string.Empty;
        _shortcutPath = shortcut ?? string.Empty;
        _writer = writer ?? new WindowsStartupLinkWriter();
        _available = available;
        _initializationError = error;
        if (available)
        {
            try
            {
                // Packaged startup is owned by Windows StartupTask. The legacy
                // shortcut is only migration state and must not make the
                // packaged registration appear enabled before the task has
                // been queried/applied.
                _enabled = _packagedStartupTask is null && File.Exists(_shortcutPath);
            }
            catch
            {
                _enabled = false;
            }
        }
    }

    public string ShortcutPath => _shortcutPath;

    internal string InstalledExecutable => _installedExecutable;

    public bool IsAvailable => _available;

    public string? InitializationError => _initializationError;

    public bool IsEnabled => Volatile.Read(ref _enabled);

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!_available)
        {
            throw new InvalidOperationException(
                "Startup registration is unavailable on this machine."
                + (_initializationError is null ? string.Empty : $" {_initializationError}"));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _disposed))
            {
                throw new ObjectDisposedException(nameof(StartupRegistrationService));
            }
            if (_packagedStartupTask is not null)
            {
                await DeleteLegacyShortcutAsync(cancellationToken);
                var applied = await _packagedStartupTask.SetEnabledAsync(enabled, cancellationToken);
                if (applied != enabled)
                {
                    throw new InvalidOperationException(
                        "Windows denied the requested packaged startup-task state.");
                }

                Volatile.Write(ref _enabled, applied);
                return;
            }
            if (enabled)
            {
                // The shortcut can be removed externally while the process is
                // still running. Do not let the cached state turn a retry into
                // a no-op in that case.
                if (_enabled && File.Exists(_shortcutPath)) return;
                await _writer.WriteAtomicAsync(
                    _shortcutPath,
                    _installedExecutable,
                    "--background",
                    cancellationToken);
            }
            else
            {
                if (!_enabled && !File.Exists(_shortcutPath)) return;
                await _writer.DeleteAsync(_shortcutPath, cancellationToken);
            }

            Volatile.Write(ref _enabled, enabled);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DeleteLegacyShortcutAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_shortcutPath)) return;

        await _writer.DeleteAsync(_shortcutPath, cancellationToken);
        if (File.Exists(_shortcutPath))
        {
            throw new IOException(
                "The legacy Dudu current-user Startup shortcut could not be removed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, true);
                _disposeTask = DrainAndDisposeAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DrainAndDisposeAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
    }
}

internal sealed class WindowsStartupTaskRegistration : IPackagedStartupTaskRegistration
{
    private const int ErrorInsufficientBuffer = 122;

    private WindowsStartupTaskRegistration()
    {
    }

    public static WindowsStartupTaskRegistration? TryCreate()
    {
        if (!OperatingSystem.IsWindows() || !HasPackageIdentity())
        {
            return null;
        }

        return new WindowsStartupTaskRegistration();
    }

    public async Task<bool> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await StartupTask.GetAsync(StartupRegistrationService.PackagedStartupTaskId)
            .AsTask(cancellationToken);
        if (enabled)
        {
            var state = IsEnabled(task.State)
                ? task.State
                : await task.RequestEnableAsync().AsTask(cancellationToken);
            return IsEnabled(state);
        }

        task.Disable();
        return IsEnabled(task.State);
    }

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result == ErrorInsufficientBuffer;
    }

    private static bool IsEnabled(StartupTaskState state) =>
        state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}

internal sealed class WindowsStartupLinkWriter : IStartupLinkWriter
{
    public Task WriteAtomicAsync(
        string shortcutPath,
        string targetPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Startup shortcuts require Windows.");
        }

        var directory = Path.GetDirectoryName(shortcutPath)
            ?? throw new InvalidOperationException("The Startup shortcut has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(shortcutPath)}.{Guid.NewGuid():N}.tmp");
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
                ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows Script Host could not be created.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                global::System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { temporary });
            if (shortcut is null)
            {
                throw new InvalidOperationException("Windows Script Host returned no shortcut object.");
            }

            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = targetPath;
            dynamicShortcut.Arguments = arguments;
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            dynamicShortcut.Save();

            // Release both RCWs before replacing the destination. WScript.Shell
            // can otherwise retain the temporary file on Windows.
            try
            {
                ReleaseComObject(shortcut);
            }
            finally
            {
                shortcut = null;
                ReleaseComObject(shell);
                shell = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, shortcutPath, overwrite: true);
        }
        finally
        {
            try
            {
                ReleaseComObject(shortcut);
            }
            finally
            {
                ReleaseComObject(shell);
            }
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // Preserve the original failure if cleanup itself is blocked.
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        return Task.CompletedTask;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
