using Npgsql;

namespace Dedupe.PgVector;

/// <summary>
/// Runs SQL on a real Postgres through Npgsql. This is the one part of the adapter that has not been run against a
/// database in this repository's tests: it is a thin wrapper, and everything above it is tested with a fake runner.
/// </summary>
public sealed class NpgsqlRunner : ISqlRunner, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlRunner(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private NpgsqlCommand Command(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        var command = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
