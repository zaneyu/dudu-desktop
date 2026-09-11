namespace Dudu.Core.Abstractions;

/// <summary>Runs a set of local persistence operations as one SQLite transaction.</summary>
public interface IAppUnitOfWork
{
    Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default);
}
