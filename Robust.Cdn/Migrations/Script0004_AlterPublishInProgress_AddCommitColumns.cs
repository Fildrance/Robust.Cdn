using Dapper;
using Microsoft.Data.Sqlite;

namespace Robust.Cdn.Migrations;

public sealed class Script0004_AlterPublishInProgress_AddCommitColumns : Migrator.IMigrationScript
{
    public string Up(IServiceProvider services, SqliteConnection connection)
    {
        connection.Execute("""
            ALTER TABLE PublishInProgress ADD COLUMN ForkUrl text null,
                                          ADD COLUMN CommitId text null,
                                          ADD COLUMN BranchName text null,
                                          ADD COLUMN EngineUrl text null,
                                          ADD COLUMN EngineCommitId text null,
                                          ADD COLUMN EngineBranchName text null;
            """);

        return string.Empty;
    }
}
