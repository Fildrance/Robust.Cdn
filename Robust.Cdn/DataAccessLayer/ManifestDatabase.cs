using Dapper;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn.DataAccessLayer;

/// <summary>
/// Database service for server manifest functionality.
/// </summary>
public sealed class ManifestDatabase(IOptions<ManifestOptions> options) : BaseScopedDatabase
{
    protected override string GetConnectionString()
    {
        return GetConnectionStringForFile(options.Value.DatabaseFileName);
    }

    public void EnsureForksCreated()
    {
        var con = Connection;
        using var tx = con.BeginTransaction();

        foreach (var forkName in options.Value.Forks.Keys)
        {
            con.Execute("INSERT INTO Fork (Name) VALUES (@Name) ON CONFLICT DO NOTHING", new { Name = forkName });
        }

        tx.Commit();
    }
}