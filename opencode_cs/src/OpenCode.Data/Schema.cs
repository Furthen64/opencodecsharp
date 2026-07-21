namespace OpenCode.Data;

public static class Schema
{
    public const string MigrationTable = @"
        CREATE TABLE IF NOT EXISTS ""migration"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""time_completed"" INTEGER NOT NULL
        );";

    public const string ProjectTable = @"
        CREATE TABLE IF NOT EXISTS ""project"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""worktree"" TEXT NOT NULL,
            ""vcs"" TEXT,
            ""name"" TEXT,
            ""icon_url"" TEXT,
            ""icon_url_override"" TEXT,
            ""icon_color"" TEXT,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL,
            ""time_initialized"" INTEGER,
            ""sandboxes"" TEXT NOT NULL,
            ""commands"" TEXT
        );";

    public const string SessionTable = @"
        CREATE TABLE IF NOT EXISTS ""session"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""project_id"" TEXT NOT NULL REFERENCES ""project""(""id""),
            ""workspace_id"" TEXT,
            ""parent_id"" TEXT,
            ""slug"" TEXT NOT NULL,
            ""directory"" TEXT NOT NULL,
            ""path"" TEXT,
            ""title"" TEXT NOT NULL,
            ""version"" TEXT NOT NULL,
            ""share_url"" TEXT,
            ""summary_additions"" INTEGER,
            ""summary_deletions"" INTEGER,
            ""summary_files"" INTEGER,
            ""summary_diffs"" TEXT,
            ""metadata"" TEXT,
            ""cost"" REAL NOT NULL DEFAULT 0,
            ""tokens_input"" INTEGER NOT NULL DEFAULT 0,
            ""tokens_output"" INTEGER NOT NULL DEFAULT 0,
            ""tokens_reasoning"" INTEGER NOT NULL DEFAULT 0,
            ""tokens_cache_read"" INTEGER NOT NULL DEFAULT 0,
            ""tokens_cache_write"" INTEGER NOT NULL DEFAULT 0,
            ""revert"" TEXT,
            ""permission"" TEXT,
            ""agent"" TEXT,
            ""model"" TEXT,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL,
            ""time_compacting"" INTEGER,
            ""time_archived"" INTEGER
        );";

    public const string MessageTable = @"
        CREATE TABLE IF NOT EXISTS ""message"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""session_id"" TEXT NOT NULL REFERENCES ""session""(""id""),
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL,
            ""data"" TEXT NOT NULL
        );";

    public const string SessionMessageTable = @"
        CREATE TABLE IF NOT EXISTS ""session_message"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""session_id"" TEXT NOT NULL REFERENCES ""session""(""id""),
            ""type"" TEXT NOT NULL,
            ""seq"" INTEGER NOT NULL,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL,
            ""data"" TEXT NOT NULL
        );";

    public const string SessionInputTable = @"
        CREATE TABLE IF NOT EXISTS ""session_input"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""session_id"" TEXT NOT NULL REFERENCES ""session""(""id""),
            ""prompt"" TEXT NOT NULL,
            ""delivery"" TEXT NOT NULL,
            ""admitted_seq"" INTEGER NOT NULL,
            ""promoted_seq"" INTEGER,
            ""time_created"" INTEGER NOT NULL
        );";

    public const string SessionContextEpochTable = @"
        CREATE TABLE IF NOT EXISTS ""session_context_epoch"" (
            ""session_id"" TEXT NOT NULL PRIMARY KEY REFERENCES ""session""(""id""),
            ""baseline"" TEXT NOT NULL,
            ""snapshot"" TEXT NOT NULL,
            ""baseline_seq"" INTEGER NOT NULL
        );";

    public const string TodoTable = @"
        CREATE TABLE IF NOT EXISTS ""todo"" (
            ""session_id"" TEXT NOT NULL REFERENCES ""session""(""id""),
            ""content"" TEXT NOT NULL,
            ""status"" TEXT NOT NULL,
            ""priority"" TEXT NOT NULL,
            ""position"" INTEGER NOT NULL,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL,
            PRIMARY KEY(""session_id"", ""position"")
        );";

    public const string EventSequenceTable = @"
        CREATE TABLE IF NOT EXISTS ""event_sequence"" (
            ""aggregate_id"" TEXT NOT NULL PRIMARY KEY,
            ""seq"" INTEGER NOT NULL,
            ""owner_id"" TEXT
        );";

    public const string EventTable = @"
        CREATE TABLE IF NOT EXISTS ""event"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""aggregate_id"" TEXT NOT NULL REFERENCES ""event_sequence""(""aggregate_id""),
            ""seq"" INTEGER NOT NULL,
            ""type"" TEXT NOT NULL,
            ""data"" TEXT NOT NULL
        );";

    public const string CredentialTable = @"
        CREATE TABLE IF NOT EXISTS ""credential"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""integration_id"" TEXT,
            ""label"" TEXT NOT NULL,
            ""value"" TEXT NOT NULL,
            ""connector_id"" TEXT,
            ""method_id"" TEXT,
            ""active"" INTEGER,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL
        );";

    public const string PermissionTable = @"
        CREATE TABLE IF NOT EXISTS ""permission"" (
            ""id"" TEXT NOT NULL PRIMARY KEY,
            ""project_id"" TEXT NOT NULL REFERENCES ""project""(""id""),
            ""action"" TEXT NOT NULL,
            ""resource"" TEXT NOT NULL,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL
        );";

    public const string SessionShareTable = @"
        CREATE TABLE IF NOT EXISTS ""session_share"" (
            ""session_id"" TEXT NOT NULL PRIMARY KEY REFERENCES ""session""(""id""),
            ""id"" TEXT NOT NULL,
            ""secret"" TEXT NOT NULL,
            ""url"" TEXT NOT NULL,
            ""time_created"" INTEGER NOT NULL,
            ""time_updated"" INTEGER NOT NULL
        );";

    public const string IndexSessionProjectId = @"
        CREATE INDEX IF NOT EXISTS ""idx_session_project_id"" ON ""session""(""project_id"");";

    public const string IndexSessionSlug = @"
        CREATE INDEX IF NOT EXISTS ""idx_session_slug"" ON ""session""(""slug"");";

    public const string IndexSessionDirectory = @"
        CREATE INDEX IF NOT EXISTS ""idx_session_directory"" ON ""session""(""directory"");";

    public const string IndexMessageSessionId = @"
        CREATE INDEX IF NOT EXISTS ""idx_message_session_id"" ON ""message""(""session_id"");";

    public const string IndexSessionMessageSessionId = @"
        CREATE INDEX IF NOT EXISTS ""idx_session_message_session_id"" ON ""session_message""(""session_id"");";

    public const string IndexSessionMessageSeq = @"
        CREATE INDEX IF NOT EXISTS ""idx_session_message_seq"" ON ""session_message""(""session_id"", ""seq"");";

    public const string IndexSessionInputSessionId = @"
        CREATE INDEX IF NOT EXISTS ""idx_session_input_session_id"" ON ""session_input""(""session_id"");";

    public const string IndexTodoSessionId = @"
        CREATE INDEX IF NOT EXISTS ""idx_todo_session_id"" ON ""todo""(""session_id"");";

    public const string IndexEventAggregateId = @"
        CREATE INDEX IF NOT EXISTS ""idx_event_aggregate_id"" ON ""event""(""aggregate_id"");";

    public const string IndexEventSeq = @"
        CREATE INDEX IF NOT EXISTS ""idx_event_seq"" ON ""event""(""aggregate_id"", ""seq"");";

    public const string IndexCredentialConnectorId = @"
        CREATE INDEX IF NOT EXISTS ""idx_credential_connector_id"" ON ""credential""(""connector_id"");";

    public const string IndexCredentialMethodId = @"
        CREATE INDEX IF NOT EXISTS ""idx_credential_method_id"" ON ""credential""(""method_id"");";

    public const string IndexPermissionProjectId = @"
        CREATE INDEX IF NOT EXISTS ""idx_permission_project_id"" ON ""permission""(""project_id"");";

    public static string[] GetAllCreateStatements()
    {
        return
        [
            MigrationTable,
            ProjectTable,
            SessionTable,
            MessageTable,
            SessionMessageTable,
            SessionInputTable,
            SessionContextEpochTable,
            TodoTable,
            EventSequenceTable,
            EventTable,
            CredentialTable,
            PermissionTable,
            SessionShareTable,
            IndexSessionProjectId,
            IndexSessionSlug,
            IndexSessionDirectory,
            IndexMessageSessionId,
            IndexSessionMessageSessionId,
            IndexSessionMessageSeq,
            IndexSessionInputSessionId,
            IndexTodoSessionId,
            IndexEventAggregateId,
            IndexEventSeq,
            IndexCredentialConnectorId,
            IndexCredentialMethodId,
            IndexPermissionProjectId,
        ];
    }
}
