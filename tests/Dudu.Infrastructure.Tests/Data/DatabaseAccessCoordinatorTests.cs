using Dudu.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// Every repository call goes through <see cref="Database.InitializeAsync"/>, which is backed by
/// the single-flight, cache-forever <see cref="DatabaseAccessCoordinator"/>. A transient failure
/// at first touch (SQLITE_BUSY/SQLITE_LOCKED, e.g. an AV scanner holding the file past
/// busy_timeout) must not be cached the same way a genuine, deterministic initialization failure
/// is -- that would brick all data access until relaunch. But unbounded retries are just as
/// dangerous the other way (H1): a PERMANENT lock (OneDrive syncing dudu.db, a second instance,
/// AV) would otherwise make every repository call re-run the whole (expensive) initializer --
/// backup-dir scan, integrity checks, 5s busy timeout -- forever. These tests exercise the
/// coordinator directly, since it is what actually decides whether a faulted initialization is
/// retried.
/// </summary>
public sealed class DatabaseAccessCoordinatorTests
{
    private static readonly TimeSpan CooldownPlusOne = TimeSpan.FromSeconds(11);

    [Fact]
    public async Task Sqlite_busy_failure_retries_once_the_cooldown_elapses()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            if (attempt == 1)
            {
                throw new SqliteException("database is locked", 5);
            }

            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(1, coordinator.InitializationRunCount);

        // A transient BUSY at first touch must not brick every later call: once the cooldown
        // between attempts has elapsed, the next InitializeAsync call retries.
        now += CooldownPlusOne;
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Sqlite_locked_failure_retries_once_the_cooldown_elapses()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            if (attempt == 1)
            {
                throw new SqliteException("database table is locked", 6);
            }

            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        now += CooldownPlusOne;
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Sqlite_busy_wrapped_as_an_inner_exception_retries_once_the_cooldown_elapses()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            if (attempt == 1)
            {
                throw new InvalidOperationException(
                    "initialization failed",
                    new SqliteException("database is locked", 5));
            }

            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync(Initializer));
        now += CooldownPlusOne;
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Sqlite_busy_wrapped_inside_an_AggregateException_is_recognized_as_transient()
    {
        // H1: IsTransientBusyOrLocked previously checked only one level of InnerException, so a
        // BUSY/LOCKED buried inside an AggregateException (e.g. from Task.Wait) was treated as a
        // deterministic failure and latched forever.
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            if (attempt == 1)
            {
                throw new AggregateException(
                    new InvalidOperationException(
                        "initialization failed",
                        new SqliteException("database is locked", 5)));
            }

            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.InitializeAsync(Initializer));
        now += CooldownPlusOne;
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Transient_failure_within_the_cooldown_is_not_retried()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            throw new SqliteException("database is locked", 5);
        }

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(1, coordinator.InitializationRunCount);

        // Advance the clock, but not past the cooldown: a repeated call must get the same
        // latched faulted task fast, not re-run the (expensive) initializer.
        now += TimeSpan.FromSeconds(5);
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(1, coordinator.InitializationRunCount);
        Assert.Equal(1, attempt);
    }

    [Fact]
    public async Task Transient_failure_latches_permanently_after_the_retry_cap()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            throw new SqliteException("database is locked", 5);
        }

        // Three consecutive transient failures, each spaced past the cooldown, are all retried.
        for (var i = 1; i <= 3; i++)
        {
            await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
            Assert.Equal(i, coordinator.InitializationRunCount);
            now += CooldownPlusOne;
        }

        // A permanently locked file (OneDrive syncing dudu.db, a second instance, AV) must not
        // re-run the whole initializer forever: past the cap it latches exactly like a
        // deterministic failure, even once the cooldown has long since elapsed again.
        now += TimeSpan.FromDays(1);
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(3, coordinator.InitializationRunCount);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task Transient_failure_counter_resets_on_success()
    {
        // A successful InitializeAsync latches permanently -- InitializeAsync never re-runs an
        // initializer that already succeeded -- so this asserts the internal counter directly
        // (Database.TransientFailureCount) rather than through a later retry, which the
        // coordinator's own contract would never allow to happen naturally.
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);
        var fail = true;

        Task Initializer()
        {
            if (fail)
            {
                throw new SqliteException("database is locked", 5);
            }

            return Task.CompletedTask;
        }

        // Two transient failures, under the cap, each retried after the cooldown.
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        now += CooldownPlusOne;
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(2, coordinator.TransientFailureCount);

        now += CooldownPlusOne;
        fail = false;
        await coordinator.InitializeAsync(Initializer);

        Assert.Equal(0, coordinator.TransientFailureCount);
    }

    [Fact]
    public async Task Transient_failure_counter_resets_on_ResetInitialization()
    {
        var now = DateTimeOffset.UtcNow;
        var coordinator = new DatabaseAccessCoordinator(() => now);

        Task Initializer() => throw new SqliteException("database is locked", 5);

        for (var i = 1; i <= 3; i++)
        {
            await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
            now += CooldownPlusOne;
        }

        // Past the cap the coordinator is latched...
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(3, coordinator.InitializationRunCount);

        // ...but an explicit ResetInitialization() (Database.InvalidateInitialization(), driven
        // by a deliberate restore/retry) re-arms it exactly like a fresh coordinator.
        coordinator.ResetInitialization();
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(4, coordinator.InitializationRunCount);
    }

    [Fact]
    public async Task Deterministic_failure_still_latches_and_is_not_retried()
    {
        // The flip side: a non-transient failure (anything other than SQLITE_BUSY/LOCKED) must
        // stay cached and faulted exactly as before -- otherwise every repository call would
        // re-run the whole initializer, including migrations and the pre-upgrade backup.
        var coordinator = new DatabaseAccessCoordinator();
        var attempt = 0;

        Task Initializer()
        {
            attempt++;
            throw new SqliteException("no such table: seed_state", 1);
        }

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(1, coordinator.InitializationRunCount);

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InitializeAsync(Initializer));
        Assert.Equal(1, coordinator.InitializationRunCount);
        Assert.Equal(1, attempt);
    }

    // Pre-handoff audit: Database.IsTransientBusyOrLocked/TransientBusyRetryCooldown are the
    // public seams the App-layer db-init startup phase uses to apply this same classification to
    // its very first InitializeAsync call, where this coordinator's own retry above can never run.
    [Fact]
    public void Public_classifier_matches_busy_and_locked_and_rejects_other_failures()
    {
        Assert.True(Database.IsTransientBusyOrLocked(new SqliteException("database is busy", 5)));
        Assert.True(Database.IsTransientBusyOrLocked(new SqliteException("database is locked", 6)));
        Assert.False(Database.IsTransientBusyOrLocked(new SqliteException("no such table: seed_state", 1)));
        Assert.False(Database.IsTransientBusyOrLocked(new InvalidOperationException("not a sqlite failure")));
    }

    [Fact]
    public void Public_classifier_walks_wrapping_exceptions_the_same_way_as_the_coordinator()
    {
        var wrapped = new InvalidOperationException(
            "wrapped", new SqliteException("database is locked", 6));
        Assert.True(Database.IsTransientBusyOrLocked(wrapped));

        var aggregate = new AggregateException(new SqliteException("database is busy", 5));
        Assert.True(Database.IsTransientBusyOrLocked(aggregate));
    }

    [Fact]
    public void Public_cooldown_matches_the_coordinators_own_retry_cooldown()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), Database.TransientBusyRetryCooldown);
    }
}
