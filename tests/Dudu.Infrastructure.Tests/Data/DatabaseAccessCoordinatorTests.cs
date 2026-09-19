using Dudu.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// Audit finding #2: every repository call goes through
/// <see cref="Database.InitializeAsync"/>, which is backed by the single-flight,
/// cache-forever <see cref="DatabaseAccessCoordinator"/>. A transient failure at first touch
/// (SQLITE_BUSY/SQLITE_LOCKED, e.g. an AV scanner holding the file past busy_timeout) must not
/// be cached the same way a genuine, deterministic initialization failure is -- that would
/// brick all data access until relaunch. These tests exercise the coordinator directly, since
/// it is what actually decides whether a faulted initialization is retried.
/// </summary>
public sealed class DatabaseAccessCoordinatorTests
{
    [Fact]
    public async Task Sqlite_busy_failure_does_not_latch_and_the_next_initialize_retries()
    {
        var coordinator = new DatabaseAccessCoordinator();
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

        // A transient BUSY at first touch must not brick every later call: the next
        // InitializeAsync call retries instead of replaying the cached fault forever.
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Sqlite_locked_failure_does_not_latch_and_the_next_initialize_retries()
    {
        var coordinator = new DatabaseAccessCoordinator();
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
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task Sqlite_busy_wrapped_as_an_inner_exception_does_not_latch()
    {
        var coordinator = new DatabaseAccessCoordinator();
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
        await coordinator.InitializeAsync(Initializer);
        Assert.Equal(2, coordinator.InitializationRunCount);
        Assert.Equal(2, attempt);
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
}
