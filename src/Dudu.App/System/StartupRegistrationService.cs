using System.Runtime.InteropServices;

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

public sealed class StartupRegistrationService : IAsyncDisposable
{
    public const string ShortcutFileName = "Dudu Desktop Companion.lnk";

    private readonly IStartupLinkWriter _writer;
    private readonly string _installedExecutable;
    private readonly string _shortcutPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _enabled;
    private bool _disposed;

    public StartupRegistrationService(
        string? installedExecutable = null,
        string? startupDirectory = null,
        IStartupLinkWriter? writer = null)
    {
        _installedExecutable = Path.GetFullPath(
            installedExecutable
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The installed executable path is unavailable."));
        var startup = startupDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startup))
        {
            throw new InvalidOperationException("The current-user Startup folder is unavailable.");
        }

        _shortcutPath = Path.Combine(startup, ShortcutFileName);
        _writer = writer ?? new WindowsStartupLinkWriter();
    }

    public string ShortcutPath => _shortcutPath;

    public bool IsEnabled => _enabled;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StartupRegistrationService));
            if (enabled)
            {
                if (_enabled) return;
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

            _enabled = enabled;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }
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
