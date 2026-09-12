using System.Runtime.ExceptionServices;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.App.Hosting;

/// <summary>
/// The process-wide owner of preference read/modify/write operations. Every
/// writer derives its change from the latest published snapshot while holding
/// the same lock, so full-record persistence cannot lose an unrelated field.
/// </summary>
public sealed class PreferenceMutationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IPreferencesRepository _repository;
    private readonly Func<Preferences, CancellationToken, Task> _applyRuntimeAsync;
    private Preferences _current;

    public PreferenceMutationCoordinator(
        Preferences initialPreferences,
        IPreferencesRepository repository,
        Func<Preferences, CancellationToken, Task>? applyRuntimeAsync = null)
    {
        _current = initialPreferences ?? throw new ArgumentNullException(nameof(initialPreferences));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _applyRuntimeAsync = applyRuntimeAsync ?? ((_, _) => Task.CompletedTask);
    }

    public Preferences Current => Volatile.Read(ref _current);

    public Task<Preferences> UpdateAsync(
        Func<Preferences, Preferences> update,
        CancellationToken cancellationToken = default) =>
        UpdateTransactionalAsync(
            update,
            (_, value, token) => _repository.SaveAsync(value, token),
            (previous, _, token) => _repository.SaveAsync(previous, token),
            applyRuntime: true,
            cancellationToken);

    public Task<Preferences> UpdateTransactionalAsync(
        Func<Preferences, Preferences> update,
        Func<Preferences, Preferences, CancellationToken, Task> persistAsync,
        Func<Preferences, Preferences, CancellationToken, Task> compensatePersistenceAsync,
        bool applyRuntime,
        CancellationToken cancellationToken = default) =>
        MutateCoreAsync(
            update,
            persistAsync,
            compensatePersistenceAsync,
            applyRuntime,
            cancellationToken);

    public Task<Preferences> CommitAsync(
        Func<Preferences, Preferences> update,
        Func<Preferences, Preferences, CancellationToken, Task> persistAsync,
        CancellationToken cancellationToken = default) =>
        MutateCoreAsync(
            update,
            persistAsync,
            static (_, _, _) => Task.CompletedTask,
            applyRuntime: false,
            cancellationToken);

    /// <summary>Runs a post-commit runtime operation under the same process-wide
    /// ordering lock. If another writer committed first, the callback receives
    /// that newer authoritative snapshot rather than a stale full record.</summary>
    public async Task<Preferences> ApplyCurrentAsync(
        Func<Preferences, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Current;
            await applyAsync(current, cancellationToken);
            return current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Preferences> MutateCoreAsync(
        Func<Preferences, Preferences> update,
        Func<Preferences, Preferences, CancellationToken, Task> persistAsync,
        Func<Preferences, Preferences, CancellationToken, Task> compensatePersistenceAsync,
        bool applyRuntime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(persistAsync);
        ArgumentNullException.ThrowIfNull(compensatePersistenceAsync);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Current;
            var updated = update(previous)
                ?? throw new InvalidOperationException("A preference mutation returned no value.");
            await persistAsync(previous, updated, cancellationToken);
            if (applyRuntime)
            {
                try
                {
                    await _applyRuntimeAsync(updated, cancellationToken);
                }
                catch (Exception applyFailure)
                {
                    try
                    {
                        await compensatePersistenceAsync(previous, updated, CancellationToken.None);
                        await _applyRuntimeAsync(previous, CancellationToken.None);
                    }
                    catch (Exception compensationFailure)
                    {
                        throw new AggregateException(
                            "Dudu could not restore preferences after runtime application failed.",
                            applyFailure,
                            compensationFailure);
                    }

                    ExceptionDispatchInfo.Capture(applyFailure).Throw();
                    throw;
                }
            }

            Volatile.Write(ref _current, updated);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }
}
