using Dudu.Core.Abstractions;
using Dudu.Infrastructure.Security;

namespace Dudu.Infrastructure.Tests.Crypto;

/// <summary>
/// A non-persistent <see cref="ISecretStore"/> used by tests that need to observe
/// how many secrets were written without touching DPAPI or the filesystem.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys that simulate an unreadable-but-present secret (e.g. a DPAPI blob left behind by a
    /// Windows password reset): <see cref="GetAsync"/> throws <see cref="SecretStoreException"/>
    /// for these instead of decoding <see cref="Values"/>, exactly like the real store's
    /// <c>ProtectedData.Unprotect</c> failing. <see cref="DeleteAsync"/> ignores this set, matching
    /// the real store, whose delete never decrypts anything.
    /// </summary>
    public HashSet<string> PoisonedKeys { get; } = new(StringComparer.Ordinal);

    public Task SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        Values[key] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (PoisonedKeys.Contains(key))
        {
            throw new SecretStoreException(
                $"The protected secret '{key}' could not be decrypted.",
                new InvalidOperationException("simulated unreadable DPAPI blob"));
        }

        // Returns a defensive copy on every call, matching the real DPAPI-backed store (which
        // decrypts a fresh buffer from disk each time): callers such as RemoteSyncService zero
        // the returned private-key bytes after use, which must not corrupt what is "stored".
        return Task.FromResult(Values.TryGetValue(key, out var value) ? value.ToArray() : null);
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }
}
