using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class TaskRepository : SqliteRepository, ITaskRepository
{
    public TaskRepository(Database database) : base(database) { }
    internal TaskRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<TaskItem?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, notes, due_utc, is_completed, created_utc, updated_utc, completed_utc FROM tasks WHERE id = $id;";
        Add(command, "$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<TaskItem>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var result = new List<TaskItem>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, notes, due_utc, is_completed, created_utc, updated_utc, completed_utc FROM tasks WHERE is_completed = 0 ORDER BY updated_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task SaveAsync(TaskItem task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (id, title, notes, due_utc, is_completed, created_utc, updated_utc, completed_utc)
            VALUES ($id, $title, $notes, $due, $completed, $created, $updated, $completedUtc)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title, notes=excluded.notes, due_utc=excluded.due_utc,
                is_completed=excluded.is_completed, created_utc=excluded.created_utc, updated_utc=excluded.updated_utc,
                completed_utc=excluded.completed_utc;
            """;
        Add(command, "$id", task.Id.ToString("D")); Add(command, "$title", task.Title); Add(command, "$notes", task.Notes);
        Add(command, "$due", Utc(task.DueUtc)); Add(command, "$completed", task.IsCompleted ? 1 : 0);
        Add(command, "$created", Utc(task.CreatedUtc)); Add(command, "$updated", Utc(task.UpdatedUtc)); Add(command, "$completedUtc", Utc(task.CompletedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TaskItem Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), ReadString(reader, 2), ReadNullableUtc(reader[3]),
        reader.GetInt32(4) != 0, ReadUtc(reader[5]), ReadUtc(reader[6]), ReadNullableUtc(reader[7]));
}
