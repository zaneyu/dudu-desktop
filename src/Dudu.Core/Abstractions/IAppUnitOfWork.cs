namespace Dudu.Core.Abstractions;

/// <summary>Runs a set of local persistence operations as one SQLite transaction.</summary>
public interface IAppUnitOfWork
{
    Task ExecuteAsync(
        Func<IAppUnitOfWorkContext, CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteAsync<TResult>(
        Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
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
