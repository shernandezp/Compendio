using Microsoft.Data.Sqlite;

namespace Compendio.Infrastructure.Search;

/// <summary>
/// Creates and drops the FTS5 objects.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately outside EF migrations. The index is a cache, not schema of record: it must be
/// droppable and rebuildable at any moment by <c>compendio reindex</c>, and a migration is exactly
/// the wrong tool for something whose correct response to damage is "rebuild it from the files".
/// Creation is idempotent and runs on every start.
/// </para>
/// <para>
/// This is one of the two files in the product allowed to contain raw SQL — FTS5 <em>is</em> raw
/// SQL, and there is no LINQ spelling of <c>bm25()</c>.
/// </para>
/// </remarks>
public static class SearchSchema
{
    public const string Table = "PagesFts";

    /// <summary>
    /// <c>remove_diacritics 2</c> is the accent-folding mode that also handles combining marks, so
    /// <c>sesion</c> matches <em>sesión</em>. <c>tokenchars '_-.'</c> keeps <c>192.168.1.1</c>,
    /// <c>VPN-Site-A</c> and <c>snake_case</c> whole, which for an IT wiki is not a detail.
    /// There is no stemmer: SQLite ships only an English one, and a bilingual corpus makes a single
    /// stemmer wrong by construction.
    /// </summary>
    private const string Tokenizer = "unicode61 remove_diacritics 2 tokenchars '_-.'";

    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(connection, $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {Table} USING fts5(
                Title, Headings, Body, Tags, Path,
                content='PageText',
                content_rowid='RowId',
                tokenize = "{Tokenizer}"
            );
            """, cancellationToken);

        // With an external-content FTS5 table the index is not maintained automatically: a delete
        // has to be told the *old* column values. Triggers on the content table are the documented
        // way to get that right, and they mean ordinary EF writes to PageText keep the index in
        // step without every call site remembering to.
        await ExecuteAsync(connection, $"""
            CREATE TRIGGER IF NOT EXISTS PageText_ai AFTER INSERT ON PageText BEGIN
                INSERT INTO {Table}(rowid, Title, Headings, Body, Tags, Path)
                VALUES (new.RowId, new.Title, new.Headings, new.Body, new.Tags, new.Path);
            END;

            CREATE TRIGGER IF NOT EXISTS PageText_ad AFTER DELETE ON PageText BEGIN
                INSERT INTO {Table}({Table}, rowid, Title, Headings, Body, Tags, Path)
                VALUES ('delete', old.RowId, old.Title, old.Headings, old.Body, old.Tags, old.Path);
            END;

            CREATE TRIGGER IF NOT EXISTS PageText_au AFTER UPDATE ON PageText BEGIN
                INSERT INTO {Table}({Table}, rowid, Title, Headings, Body, Tags, Path)
                VALUES ('delete', old.RowId, old.Title, old.Headings, old.Body, old.Tags, old.Path);
                INSERT INTO {Table}(rowid, Title, Headings, Body, Tags, Path)
                VALUES (new.RowId, new.Title, new.Headings, new.Body, new.Tags, new.Path);
            END;
            """, cancellationToken);

        // Outbound links, for the backlinks panel. Not an FTS table: it is an exact-match lookup,
        // and it is filtered by the same permission predicate as everything else.
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS PageLinks (
                -- NOCASE because the GUID text representation EF writes and the one we write here
                -- agree on the value but not necessarily on the case.
                PageId  TEXT NOT NULL COLLATE NOCASE,
                Target  TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (PageId, Target)
            );
            CREATE INDEX IF NOT EXISTS IX_PageLinks_Target ON PageLinks(Target);
            """, cancellationToken);
    }

    /// <summary>Drops the index. Everything it held is reconstructible from the content folder.</summary>
    public static async Task DropAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Table};", cancellationToken);

    /// <summary>
    /// Rebuilds the FTS index from <c>PageText</c> without touching the extracted text itself.
    /// </summary>
    public static async Task RebuildAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(connection, $"INSERT INTO {Table}({Table}) VALUES('rebuild');", cancellationToken);

    /// <summary>
    /// Removes the opted-in secure scopes' text in one statement.
    /// </summary>
    /// <remarks>
    /// This is what <c>compendio reindex --drop-secure</c> calls. Deleting the extracted text is
    /// what matters: it is the copy of the plaintext that indexing put inside <c>compendio.db</c>,
    /// and the FTS rows follow it.
    /// </remarks>
    public static async Task DropSecureAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(connection, $"""
            DELETE FROM {Table} WHERE rowid IN (
                SELECT t.RowId FROM PageText t
                JOIN Pages p ON p.Id = t.PageId
                WHERE p.IsSecure = 1);

            DELETE FROM PageText WHERE PageId IN (SELECT Id FROM Pages WHERE IsSecure = 1);
            """, cancellationToken);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
