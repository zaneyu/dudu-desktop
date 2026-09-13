using Dudu.Core.Abstractions;

namespace Dudu.Infrastructure.Tests.Crypto;

/// <summary>
/// A non-persistent <see cref="ISecretStore"/> used by tests that need to observe
/// how many secrets were written without touching DPAPI or the filesystem.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

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
