using Dudu.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Dudu.Infrastructure.Data.Repositories;

namespace Dudu.Infrastructure.Data;

public sealed class AppUnitOfWork(Database database) : IAppUnitOfWork
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task ExecuteAsync(Func<IAppUnitOfWorkContext, CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (context, token) => { await action(context, token); return null; }, cancellationToken);

    public async Task<TResult> ExecuteAsync<TResult>(Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = await _database.CreateConnectionAsync(cancellationToken);
        // DEFERRED (the Microsoft.Data.Sqlite default) is sufficient here because every repository
        // call in the action runs on this one bound connection/transaction, and contention between
        // concurrent units of work is resolved by SQLite locking with the 5-second busy timeout.
        // File replacement (backup restore, full wipe) holds the maintenance lease, which drains
        // and blocks connections, so no unit of work runs concurrently with those operations.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var context = new AppUnitOfWorkContext(_database, connection, transaction);
        try
        {
            var result = await action(context, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    // There is intentionally no ExecuteAsync(Func<CancellationToken, Task>) overload: a callback
    // without the IAppUnitOfWorkContext would open its own connections outside this transaction,
    // so commit/rollback would cover an empty transaction while the real work auto-committed
    // elsewhere. Callers needing raw connection access use ExecuteInTransactionAsync below.

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
    public Task ExecuteAsync(Func<IAppUnitOfWorkContext, CancellationToken, Task> action, CancellationToken cancellationToken = default) => _inner.ExecuteAsync(action, cancellationToken);
    public Task<TResult> ExecuteAsync<TResult>(Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default) => _inner.ExecuteAsync(action, cancellationToken);
}

internal sealed class AppUnitOfWorkContext : IAppUnitOfWorkContext
{
    public AppUnitOfWorkContext(Database database, SqliteConnection connection, SqliteTransaction transaction)
    {
        var transactionContext = new SqliteTransactionContext(connection, transaction);
        CheckIns = new CheckInRepository(database, transactionContext);
        Countdowns = new CountdownRepository(database, transactionContext);
        FocusSessions = new FocusSessionRepository(database, transactionContext);
        LocalNotes = new LocalNoteRepository(database, transactionContext);
        PetPlacements = new PetPlacementRepository(database, transactionContext);
        Preferences = new PreferencesRepository(database, transactionContext);
        Profiles = new ProfileRepository(database, transactionContext);
        RemoteEnvelopes = new RemoteEnvelopeRepository(database, transactionContext);
        Reminders = new ReminderRepository(database, transactionContext);
        Tasks = new TaskRepository(database, transactionContext);
    }

    public ICheckInRepository CheckIns { get; }
    public ICountdownRepository Countdowns { get; }
    public IFocusSessionRepository FocusSessions { get; }
    public ILocalNoteRepository LocalNotes { get; }
    public IPetPlacementRepository PetPlacements { get; }
    public IPreferencesRepository Preferences { get; }
    public IProfileRepository Profiles { get; }
    public IRemoteEnvelopeRepository RemoteEnvelopes { get; }
    public IReminderRepository Reminders { get; }
    public ITaskRepository Tasks { get; }
}
