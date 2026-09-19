using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Security;

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// DPAPI (CurrentUser scope) file-backed secret store. Ciphertext files live under
/// <c>_directory</c>, whose ACL is restricted to the current user on every write.
/// </summary>
/// <remarks>
/// SAME-USER LIMITATION: DPAPI CurrentUser scope and the directory ACL both trust the OS user
/// boundary. Any process running as the same Windows user can read these files -- this store
/// protects secrets from other users and from offline disk access, but it is not a sandbox
/// boundary against same-user malware. Key material held in memory (see RelayClient,
/// RemoteSyncService) is zeroed promptly for the same reason: defense in depth, not isolation.
/// </remarks>
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>
    /// The <c>StartupFailureLogger</c> phase for secret-read failures
    /// (<see cref="GetAsync"/>). See <see cref="FailureReporter"/>.
    /// </summary>
    public const string ReadFailurePhase = "secret-read";

    /// <summary>
    /// The <c>StartupFailureLogger</c> phase for secret-mutation failures
    /// (<see cref="SetAsync"/> and <see cref="DeleteAsync"/>). Deletes share
    /// the write phase: a failed delete leaves a mutation unapplied, and the
    /// registration completion-marker ordering (token before device id) is
    /// unaffected by reporting — failures still propagate exactly as before.
    /// </summary>
    public const string WriteFailurePhase = "secret-write";

    private static readonly Regex KeyPattern = new(
        "\\A[a-z0-9-]{1,64}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>
    /// Optional failure hook invoked with (phase, exception) when a secret
    /// read, write, or delete fails. Logging only: failures still propagate
    /// to the caller exactly as before, so a failing secret write during
    /// registration still leaves the device-id-last completion-marker
    /// ordering intact. Never receives secret material — only the phase name
    /// and the exception (whose messages carry key names at most, never key
    /// bytes). A plain delegate, not an <c>ILogger</c>: no logging dependency
    /// enters the secret store. Absent reads (missing file) and idempotent
    /// deletes are not failures and are never reported, nor are
    /// cancellations or argument-validation errors.
    /// </summary>
    public Action<string, Exception>? FailureReporter { get; set; }

    public async Task SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetPath(key);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var plaintext = value.ToArray();
        var entropy = Encoding.UTF8.GetBytes("DuduDesktop:v1:" + key);

        try
        {
            Directory.CreateDirectory(_directory);
            RestrictDirectoryToCurrentUser(_directory);
            var protectedBytes = ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(protectedBytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                ReplaceAtomically(temporaryPath, path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(WriteFailurePhase, exception);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public async Task<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        byte[] protectedBytes;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(GetPath(key), cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // No exists-then-read race: a secret deleted concurrently simply reads as absent.
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(ReadFailurePhase, exception);
            throw;
        }

        var entropy = Encoding.UTF8.GetBytes("DuduDesktop:v1:" + key);
        try
        {
            try
            {
                return ProtectedData.Unprotect(
                    protectedBytes,
                    entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException exception)
            {
                throw new SecretStoreException(
                    $"The protected secret '{key}' could not be decrypted.",
                    exception);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                throw new SecretStoreException(
                    $"The protected secret '{key}' is corrupt.",
                    exception);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(ReadFailurePhase, exception);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Delete is idempotent: a missing file -- or a missing secrets directory, when no
            // secret was ever written -- is already the desired end state, including under a
            // concurrent delete racing this one.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(WriteFailurePhase, exception);
            throw;
        }

        return Task.CompletedTask;
    }

    private string GetPath(string key) => Path.Combine(_directory, key + ".bin");

    /// <summary>
    /// Defense-in-depth on top of DPAPI (which already binds ciphertext to the Windows user):
    /// strips inherited ACEs from the secrets directory and grants full control to the current
    /// user only, so other local users cannot even list the file names. Best-effort: if
    /// hardening fails the write still proceeds under DPAPI protection rather than failing
    /// closed and wedging registration.
    /// </summary>
    private static void RestrictDirectoryToCurrentUser(string directory)
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent()?.Name;
            if (string.IsNullOrWhiteSpace(identity))
            {
                return;
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException
            or InvalidOperationException
            or IdentityNotMappedException)
        {
            // Best effort (see above): DPAPI remains the primary protection.
        }
    }

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!KeyPattern.IsMatch(key))
        {
            throw new ArgumentException(
                "Secret keys must match ^[a-z0-9-]{1,64}$.",
                nameof(key));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required.");
        }
    }

    private void ReportFailure(string phase, Exception exception)
    {
        try
        {
            FailureReporter?.Invoke(phase, exception);
        }
        catch
        {
            // Failure reporting must never change secret-store behavior.
        }
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        // A same-volume rename is atomic and avoids exposing a partially-written
        // secret. File.Move(..., overwrite: true) uses the platform replacement
        // primitive on Windows.
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the original failure. A leftover temporary file contains
            // protected bytes only and is harmless to a subsequent write.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original failure; do not remove the destination file.
        }
    }
}
