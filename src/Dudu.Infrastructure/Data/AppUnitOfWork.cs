using Dudu.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public sealed class AppUnitOfWork(Database database) : IAppUnitOfWork
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async token => { await action(token); return null; }, cancellationToken);

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = await _database.CreateConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // The callback is deliberately passed the transaction cancellation token;
            // repository-specific atomic work is exposed by their transactional methods.
            var result = await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task ExecuteInTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = await _database.CreateConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try { await action(connection, transaction, cancellationToken); await transaction.CommitAsync(cancellationToken); }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }
}

public sealed class SqliteAppUnitOfWork(Database database) : IAppUnitOfWork
{
    private readonly AppUnitOfWork _inner = new(database);
    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) => _inner.ExecuteAsync(action, cancellationToken);
    public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default) => _inner.ExecuteAsync(action, cancellationToken);
}
