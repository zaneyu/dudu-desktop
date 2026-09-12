using System.Security.Cryptography;
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

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly Regex KeyPattern = new(
        "\\A[a-z0-9-]{1,64}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_directory);
        var path = GetPath(key);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var plaintext = value.ToArray();
        var entropy = Encoding.UTF8.GetBytes("DuduDesktop:v1:" + key);

        try
        {
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
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
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
        File.Delete(GetPath(key));
        return Task.CompletedTask;
    }

    private string GetPath(string key) => Path.Combine(_directory, key + ".bin");

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
