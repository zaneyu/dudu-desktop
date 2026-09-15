namespace Dudu.Core.Abstractions;

/// <summary>Runs a set of local persistence operations as one SQLite transaction.
///
/// Every operation must go through the <see cref="IAppUnitOfWorkContext"/> repositories: the
/// context binds all writes to the single connection/transaction the unit of work opened, so a
/// commit or rollback actually covers the work. There is deliberately no
/// <c>ExecuteAsync(Func{CancellationToken, Task})</c> overload — a callback without the context
/// would open its own connections outside the transaction, and committing the (empty)
/// transaction would silently claim atomicity the work never had.
///
/// Writer discipline: SQLite uses DEFERRED transactions upgraded on first write, with a 5-second
/// busy timeout (<c>Database.ConfigureConnection</c>), so concurrent units of work contend on
/// SQLite locks rather than corrupting data. Callers must not assume snapshot isolation across
/// concurrent units of work. File-level operations (backup, restore, full wipe) hold the
/// database maintenance lease, which drains connections and blocks new ones, so no unit of work
/// runs while the underlying database file is being replaced or deleted.
/// </summary>
public interface IAppUnitOfWork
{
    Task ExecuteAsync(
        Func<IAppUnitOfWorkContext, CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteAsync<TResult>(
        Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default);
}

/// <summary>Repository instances bound to one connection and transaction.</summary>
public interface IAppUnitOfWorkContext
{
    ICheckInRepository CheckIns { get; }
    ICountdownRepository Countdowns { get; }
    IFocusSessionRepository FocusSessions { get; }
    ILocalNoteRepository LocalNotes { get; }
    IPetPlacementRepository PetPlacements { get; }
    IPreferencesRepository Preferences { get; }
    IProfileRepository Profiles { get; }
    IRemoteEnvelopeRepository RemoteEnvelopes { get; }
    IReminderRepository Reminders { get; }
    ITaskRepository Tasks { get; }
}
