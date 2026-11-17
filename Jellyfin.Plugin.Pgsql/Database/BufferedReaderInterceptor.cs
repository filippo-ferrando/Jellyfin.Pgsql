using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Fully reads the results of a few known queries into memory and closes their reader right away.
/// </summary>
/// <remarks>
/// Some Jellyfin server code keeps a query's reader open while it runs further commands on the same context,
/// which SQLite allows but PostgreSQL does not: Npgsql has no multiple active result sets and fails with
/// "A command is already in progress". Buffering those queries' (small) results restores the behaviour the
/// server code expects, without changing how any other query streams its results.
/// </remarks>
internal sealed class BufferedReaderInterceptor : DbCommandInterceptor
{
    private static readonly HashSet<string> _bufferedQueries = new(StringComparer.Ordinal)
    {
        // MigrateRatingLevels (20260915120000) runs ExecuteUpdate for every row of this query while enumerating it.
        "SELECT DISTINCT b.\"OfficialRating\" FROM \"BaseItems\" AS b",
    };

    /// <inheritdoc/>
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        return ShouldBuffer(command) ? Buffer(result) : result;
    }

    /// <inheritdoc/>
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        return ValueTask.FromResult(ShouldBuffer(command) ? Buffer(result) : result);
    }

    private static bool ShouldBuffer(DbCommand command)
    {
        var normalized = string.Join(' ', command.CommandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return _bufferedQueries.Contains(normalized.TrimEnd(';'));
    }

    private static DataTableReader Buffer(DbDataReader result)
    {
        var table = new DataTable();
        using (result)
        {
            table.Load(result);
        }

        return table.CreateDataReader();
    }
}
