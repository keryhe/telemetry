using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// A <see cref="DuckDBConnection"/> whose commands accept <c>@name</c> parameters. DuckDB binds <c>$name</c>, while the
/// shared read SQL (<c>RollupReadRepositoryBase</c>, Dapper's list expansion) is written with <c>@name</c> like every other
/// provider's, so each command's text is rewritten as it is set. Single-quoted string literals are left alone.
/// </summary>
public sealed partial class NamedParameterConnection(DuckDBConnection inner, ILogger? statementLog = null) : DbConnection
{
    [GeneratedRegex(@"'(?:[^']|'')*'|@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Tokens();

    internal static string Rewrite(string sql)
        => Tokens().Replace(sql, m => m.Groups[1].Success ? "$" + m.Groups[1].Value : m.Value);

    public DuckDBConnection Inner { get; } = inner;

    /// <summary>Files the views defined on this connection read; logged with each statement.</summary>
    internal int FilesRead { get; set; }

    private bool Timing => statementLog is { } log && log.IsEnabled(LogLevel.Debug);

    private T Timed<T>(string sql, Func<T> run)
    {
        if (!Timing) return run();
        var started = Stopwatch.GetTimestamp();
        try { return run(); }
        finally { LogStatement(sql, started); }
    }

    private async Task<T> TimedAsync<T>(string sql, Func<Task<T>> run)
    {
        if (!Timing) return await run();
        var started = Stopwatch.GetTimestamp();
        try { return await run(); }
        finally { LogStatement(sql, started); }
    }

    private void LogStatement(string sql, long started)
    {
        // One line: the harness parses "parquet-sql ms=<n> files=<n> sql=<text>" out of the host's console log.
        var text = Whitespace().Replace(sql, " ").Trim();
        if (text.Length > 400) text = text[..400];
        statementLog!.LogDebug("parquet-sql ms={Ms} files={Files} sql={Sql}",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), FilesRead, text);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [AllowNull]
    public override string ConnectionString { get => Inner.ConnectionString; set => Inner.ConnectionString = value; }
    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override ConnectionState State => Inner.State;
    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);
    public override void Close() => Inner.Close();
    public override void Open() => Inner.Open();
    public override Task OpenAsync(CancellationToken cancellationToken) => Inner.OpenAsync(cancellationToken);
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Inner.BeginTransaction(isolationLevel);
    protected override DbCommand CreateDbCommand() => new Command(this, Inner.CreateCommand());

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class Command(NamedParameterConnection connection, DbCommand inner) : DbCommand
    {
        private string _text = "";

        [AllowNull]
        public override string CommandText
        {
            get => _text;
            set { _text = value ?? ""; inner.CommandText = Rewrite(_text); }
        }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() => connection.Timed(_text, inner.ExecuteNonQuery);
        public override object? ExecuteScalar() => connection.Timed(_text, inner.ExecuteScalar);
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => connection.Timed(_text, () => inner.ExecuteReader(behavior));
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
            => connection.TimedAsync(_text, () => inner.ExecuteReaderAsync(behavior, cancellationToken));
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
            => connection.TimedAsync(_text, () => inner.ExecuteNonQueryAsync(cancellationToken));
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
            => connection.TimedAsync(_text, () => inner.ExecuteScalarAsync(cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
