namespace Dudu.Core.Abstractions;

public interface ISecretStore
{
    Task SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);
}
