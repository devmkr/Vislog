// Copyright (C) 2017 Alaa Masoud
// See the LICENSE file in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using Sejil.Configuration;
using Sejil.Data.Query;
using Sejil.Models;
using Serilog.Events;

namespace Sejil.Data;

public abstract class SejilRepository : ISejilRepository
{
    protected SejilSettings Settings { get; }
    private readonly string _createDatabaseSql;

    public SejilRepository(SejilSettings settings)
    {
        Settings = settings;
        _createDatabaseSql = ResourceHelper.GetEmbeddedResource(GetCreateDatabaseSqlResourceName());
        InitializeDatabase();
    }

    protected abstract DbConnection GetConnection();

    protected abstract string GetCreateDatabaseSqlResourceName();

    protected abstract string GetPaginSql(int offset, int take);

    protected abstract string GetDateTimeOffsetSql(int value, string unit);

    public async Task<IEnumerable<LogQuery>> GetSavedQueriesAsync()
    {
        const string Sql = "SELECT * FROM log_query";

        using var conn = GetConnection();
        await conn.OpenAsync();
        return await conn.QueryAsync<LogQuery>(Sql);
    }

    public async Task<bool> SaveQueryAsync(LogQuery logQuery)
    {
        const string Sql = "INSERT INTO log_query (name, query) VALUES (@name, @query)";

        using var conn = GetConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandType = CommandType.Text;
        cmd.AddParameterWithValue("@name", logQuery.Name);
        cmd.AddParameterWithValue("@query", logQuery.Query);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteQueryAsync(string queryName)
    {
        const string Sql = "DELETE FROM log_query WHERE name = @name";

        using var conn = GetConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandType = CommandType.Text;
        cmd.AddParameterWithValue("@name", queryName);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IEnumerable<LogEntry>> GetEventsPageAsync(int page, DateTime? startingTimestamp, LogQueryFilter queryFilter)
    {
        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Argument must be greater than zero.");
        }

        var sql = GetPagedLogEntriesSql(page, Settings.PageSize, startingTimestamp, queryFilter);

        using var conn = GetConnection();
        await conn.OpenAsync();
        var lookup = new Dictionary<string, LogEntry>();

        await conn.QueryAsync<LogEntry, LogEntryProperty, LogEntry>(sql, (l, p) =>
        {
            if (!lookup.TryGetValue(l.Id, out var logEntry))
            {
                lookup.Add(l.Id, logEntry = l);
            }

            if (p is not null)
            {
                logEntry.Properties.Add(p);
            }
            return logEntry;

        });

        return lookup.Values;
    }

    public async Task InsertEventsAsync(IEnumerable<LogEvent> events)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        using var tran = conn.BeginTransaction();
        using (var cmdLogEntry = CreateLogEntryInsertCommand(conn, tran))
        using (var cmdLogEntryProperty = CreateLogEntryPropertyInsertCommand(conn, tran))
        {
            foreach (var logEvent in events)
            {
                // Do not log events that were generated from browsing Sejil URL.
                if (logEvent.Properties.Any(p => (p.Key == "RequestPath" || p.Key == "Path") &&
                    p.Value.ToString().Contains(Settings.Url, StringComparison.Ordinal)))
                {
                    continue;
                }

                var logId = await InsertLogEntryAsync(cmdLogEntry, logEvent);
                foreach (var property in logEvent.Properties)
                {
                    await InsertLogEntryPropertyAsync(cmdLogEntryProperty, logId, property);
                }
            }
        }
        await tran.CommitAsync();
    }

    internal string GetPagedLogEntriesSql(int page, int pageSize, DateTime? startingTimestamp, LogQueryFilter queryFilter)
    {
        var timestampWhereClause = TimestampWhereClause();
        var queryWhereClause = QueryWhereClause();

        return
$@"SELECT l.*, p.* from 
(
    SELECT * FROM log
    {timestampWhereClause}
    {queryWhereClause}{FiltersWhereClause()}
    ORDER BY timestamp DESC
    {GetPaginSql((page - 1) * pageSize, pageSize)}
) l
LEFT JOIN log_property p ON l.id = p.logId
ORDER BY l.timestamp DESC, p.name";

        string TimestampWhereClause()
        {
            var hasDateFilter = queryFilter.DateFilter is not null || queryFilter.DateRangeFilter is not null;

            if (startingTimestamp.HasValue || hasDateFilter)
            {
                var sql = new StringBuilder();
                sql.Append("WHERE (");

                if (startingTimestamp.HasValue)
                {
                    sql.AppendFormat(CultureInfo.InvariantCulture, "timestamp <= '{0:yyyy-MM-dd HH:mm:ss.fff}'", startingTimestamp.Value);
                }
                if (startingTimestamp.HasValue && hasDateFilter)
                {
                    sql.Append(" AND ");
                }
                if (hasDateFilter)
                {
                    sql.Append(BuildDateFilter(queryFilter));
                }

                sql.Append(')');
                return sql.ToString();
            }

            return string.Empty;
        }

        string QueryWhereClause() =>
            string.IsNullOrWhiteSpace(queryFilter.QueryText)
                ? ""
                : timestampWhereClause.Length > 0
                    ? $"AND ({QueryEngine.Translate(queryFilter.QueryText, (CodeGenerator)Activator.CreateInstance(Settings.CodeGeneratorType)!)})"
                    : $"WHERE ({QueryEngine.Translate(queryFilter.QueryText, (CodeGenerator)Activator.CreateInstance(Settings.CodeGeneratorType)!)})";

        string FiltersWhereClause() =>
            string.IsNullOrWhiteSpace(queryFilter.LevelFilter) && (!queryFilter.ExceptionsOnly)
                ? ""
                : timestampWhereClause.Length > 0 || queryWhereClause.Length > 0
                    ? $" AND ({BuildFilterWhereClause(queryFilter.LevelFilter, queryFilter.ExceptionsOnly)})"
                    : $"WHERE ({BuildFilterWhereClause(queryFilter.LevelFilter, queryFilter.ExceptionsOnly)})";
    }

    private static string BuildFilterWhereClause(string? levelFilter, bool exceptionsOnly)
    {
        var sp = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(levelFilter))
        {
            sp.AppendFormat(CultureInfo.InvariantCulture, "level = '{0}'", levelFilter);
        }

        if (exceptionsOnly && sp.Length > 0)
        {
            sp.Append(" AND ");
        }

        if (exceptionsOnly)
        {
            sp.Append("exception is not null");
        }

        return sp.ToString();
    }

    private string BuildDateFilter(LogQueryFilter queryFilter)
    {
        if (queryFilter.DateFilter != null)
        {
            return queryFilter.DateFilter switch
            {
                "5m" => $"timestamp >= {GetDateTimeOffsetSql(-5, "minute")}",
                "15m" => $"timestamp >= {GetDateTimeOffsetSql(-15, "minute")}",
                "1h" => $"timestamp >= {GetDateTimeOffsetSql(-1, "hour")}",
                "6h" => $"timestamp >= {GetDateTimeOffsetSql(-6, "hour")}",
                "12h" => $"timestamp >= {GetDateTimeOffsetSql(-12, "hour")}",
                "24h" => $"timestamp >= {GetDateTimeOffsetSql(-24, "hour")}",
                "2d" => $"timestamp >= {GetDateTimeOffsetSql(-2, "day")}",
                "5d" => $"timestamp >= {GetDateTimeOffsetSql(-5, "day")}",
                _ => "",
            };
        }
        else if (queryFilter.DateRangeFilter != null)
        {
            return $"timestamp >= '{queryFilter.DateRangeFilter[0]:yyyy-MM-dd}' and timestamp < '{queryFilter.DateRangeFilter[1]:yyyy-MM-dd}'";
        }

        return "";
    }

    private void InitializeDatabase()
    {
        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _createDatabaseSql;
        cmd.ExecuteNonQuery();
    }

    private static async Task<string> InsertLogEntryAsync(DbCommand cmd, LogEvent log)
    {
        var id = Guid.NewGuid().ToString();

        cmd.Parameters["@id"].Value = id;
        cmd.Parameters["@message"].Value = log.MessageTemplate.Render(log.Properties);
        cmd.Parameters["@messageTemplate"].Value = log.MessageTemplate.Text;
        cmd.Parameters["@level"].Value = log.Level.ToString();
        cmd.Parameters["@timestamp"].Value = log.Timestamp.ToUniversalTime().DateTime;
        cmd.Parameters["@exception"].Value = log.Exception?.Demystify().ToString() ?? (object)DBNull.Value;

        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertLogEntryPropertyAsync(DbCommand cmd, string logId, KeyValuePair<string, LogEventPropertyValue> property)
    {
        cmd.Parameters["@logId"].Value = logId;
        cmd.Parameters["@name"].Value = property.Key;
        cmd.Parameters["@value"].Value = StripStringQuotes(property.Value.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private static DbCommand CreateLogEntryInsertCommand(DbConnection conn, DbTransaction tran)
    {
        const string Sql = "INSERT INTO log (id, message, messageTemplate, level, timestamp, exception)" +
            "VALUES (@id, @message, @messageTemplate, @level, @timestamp, @exception);";

        var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandType = CommandType.Text;
        cmd.Transaction = tran;

        cmd.AddParameterWithType("@id", DbType.String);
        cmd.AddParameterWithType("@message", DbType.String);
        cmd.AddParameterWithType("@messageTemplate", DbType.String);
        cmd.AddParameterWithType("@level", DbType.String);
        cmd.AddParameterWithType("@timestamp", DbType.DateTime2);
        cmd.AddParameterWithType("@exception", DbType.String);

        return cmd;
    }

    private static DbCommand CreateLogEntryPropertyInsertCommand(DbConnection conn, DbTransaction tran)
    {
        const string Sql = "INSERT INTO log_property (logId, name, value)" +
            "VALUES (@logId, @name, @value);";

        var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandType = CommandType.Text;
        cmd.Transaction = tran;

        cmd.AddParameterWithType("@logId", DbType.String);
        cmd.AddParameterWithType("@name", DbType.String);
        cmd.AddParameterWithType("@value", DbType.String);

        return cmd;
    }

    private static string StripStringQuotes(string value)
        => value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}
