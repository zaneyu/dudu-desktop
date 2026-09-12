using System.Globalization;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public abstract class SqliteRepository
{
    private readonly SqliteTransactionContext? _transactionContext;

    protected SqliteRepository(Database database)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
    }

    internal SqliteRepository(Database database, SqliteTransactionContext transactionContext)
        : this(database)
    {
        _transactionContext = transactionContext ?? throw new ArgumentNullException(nameof(transactionContext));
    }

    protected Database Database { get; }

    protected bool IsTransactionBound => _transactionContext is not null;

    protected SqliteTransaction? Transaction => _transactionContext?.Transaction;

    protected async Task<SqliteConnectionLease> OpenAsync(CancellationToken cancellationToken)
    {
        if (_transactionContext is not null)
        {
            return SqliteConnectionLease.Bound(_transactionContext.Connection, _transactionContext.Transaction);
        }

        return SqliteConnectionLease.Owned(await Database.CreateConnectionAsync(cancellationToken));
    }

    protected static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    protected static string? Utc(DateTimeOffset? value) => value is null ? null : Utc(value.Value);

    protected static DateTimeOffset ReadUtc(object value) =>
        DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    protected static DateTimeOffset? ReadNullableUtc(object? value) =>
        value is null || value is DBNull ? null : ReadUtc(value);

    protected static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    protected static DateOnly ReadDate(object value) =>
        DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    protected static string Time(TimeOnly value) => value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    protected static TimeOnly ReadTime(object value) =>
        TimeOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    protected static object Db(object? value) => value ?? DBNull.Value;

    protected static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, Db(value));

    protected static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    protected static int WeekdayMask(IReadOnlySet<DayOfWeek> days) =>
        days.Aggregate(0, (mask, day) => mask | (1 << (int)day));

    protected static IReadOnlySet<DayOfWeek> ReadWeekdays(int mask) =>
        Enum.GetValues<DayOfWeek>().Where(day => (mask & (1 << (int)day)) != 0).ToHashSet();

    protected static (int Kind, string? LocalTime, int? Weekdays, long? IntervalSeconds, string? FirstDueUtc)
        EncodeRule(RecurrenceRule rule)
    {
        return rule switch
        {
            RecurrenceRule.Once => (0, null, null, null, null),
            RecurrenceRule.Daily daily => (1, Time(daily.LocalTime), null, null, null),
            RecurrenceRule.SelectedWeekdays selected =>
                (2, Time(selected.LocalTime), WeekdayMask(selected.Days), null, null),
            RecurrenceRule.Interval interval =>
                (3, null, null, interval.Period.Ticks, Utc(interval.FirstDueUtc)),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), "Unknown recurrence rule."),
        };
    }

    protected static RecurrenceRule DecodeRule(
        int kind,
        object? localTime,
        object? weekdays,
        object? intervalSeconds,
        object? firstDueUtc)
    {
        return kind switch
        {
            0 => new RecurrenceRule.Once(),
            1 => new RecurrenceRule.Daily(ReadTime(localTime!)),
            2 => new RecurrenceRule.SelectedWeekdays(
                ReadWeekdays(Convert.ToInt32(weekdays, CultureInfo.InvariantCulture)),
                ReadTime(localTime!)),
            3 => new RecurrenceRule.Interval(
                TimeSpan.FromTicks(Convert.ToInt64(intervalSeconds, CultureInfo.InvariantCulture)),
                ReadNullableUtc(firstDueUtc)),
            _ => throw new InvalidDataException($"Unknown reminder recurrence kind {kind}."),
        };
    }

    protected static QuietHours? ReadQuietHours(
        object? enabled,
        object? start,
        object? end)
    {
        if (enabled is null || enabled is DBNull)
        {
            return null;
        }

        return new QuietHours(
            Convert.ToInt32(enabled, CultureInfo.InvariantCulture) != 0,
            ReadTime(start!),
            ReadTime(end!));
    }
}

internal sealed class SqliteTransactionContext(
    SqliteConnection connection,
    SqliteTransaction transaction)
{
    public SqliteConnection Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));
    public SqliteTransaction Transaction { get; } = transaction ?? throw new ArgumentNullException(nameof(transaction));
}

public sealed class SqliteConnectionLease : IAsyncDisposable
{
    private readonly bool _ownsConnection;

    private SqliteConnectionLease(SqliteConnection connection, bool ownsConnection, SqliteTransaction? transaction = null)
    {
        Connection = connection;
        _ownsConnection = ownsConnection;
        Transaction = transaction;
    }

    public SqliteConnection Connection { get; }

    private SqliteTransaction? Transaction { get; }

    public static SqliteConnectionLease Bound(SqliteConnection connection, SqliteTransaction? transaction = null) => new(connection, false, transaction);

    public static SqliteConnectionLease Owned(SqliteConnection connection) => new(connection, true);

    public SqliteCommand CreateCommand()
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        return command;
    }

    public async Task<SqliteTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        (SqliteTransaction)await Connection.BeginTransactionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
