namespace Keryhe.Telemetry.Sqlite;

/// <summary>Where a SQLite control-plane file stands against this build's schema version.</summary>
public enum SqliteSchemaState
{
    /// <summary>The file holds exactly this build's version.</summary>
    Current,
    /// <summary>The file does not exist.</summary>
    MissingFile,
    /// <summary>The file exists but records no control-plane version (empty, or not created by this schema).</summary>
    NotInitialized,
    /// <summary>The file records an older version: the API host migrates it.</summary>
    Older,
    /// <summary>The file records a newer version than this build knows: downgrades are not supported.</summary>
    Newer
}

/// <summary>The result of <see cref="SqliteControlPlaneSchema.CheckAsync"/>.</summary>
public sealed record SqliteSchemaCheck(SqliteSchemaState State, string DataSource, string? RecordedVersion, string TargetVersion)
{
    public bool IsCurrent => State == SqliteSchemaState.Current;

    /// <summary>One sentence naming the file, what is wrong and what to do about it (empty when current).</summary>
    public string Message => State switch
    {
        SqliteSchemaState.Current => string.Empty,
        SqliteSchemaState.MissingFile =>
            $"The SQLite control-plane database '{DataSource}' does not exist. Start the API host once to create it " +
            $"(or run schema/apply-schema.sh controlplane sqlite '{DataSource}'), then start this host.",
        SqliteSchemaState.NotInitialized =>
            $"The SQLite control-plane database '{DataSource}' exists but has no control-plane schema. Start the API host once to create it, then start this host.",
        SqliteSchemaState.Older =>
            $"The SQLite control-plane database '{DataSource}' is at schema version {RecordedVersion} but this build needs {TargetVersion}. " +
            "Start the API host to migrate it, then start this host.",
        _ =>
            $"The SQLite control-plane database '{DataSource}' is at schema version {RecordedVersion}, newer than this build's {TargetVersion}. " +
            "Downgrades are not supported; use a build at least as new as the database."
    };

    /// <summary>Throws an <see cref="InvalidOperationException"/> carrying <see cref="Message"/> unless the file is current.</summary>
    public void ThrowIfNotCurrent()
    {
        if (!IsCurrent) throw new InvalidOperationException(Message);
    }
}
