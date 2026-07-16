using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn.DataAccessLayer;

/// <summary>
/// Database service for CDN functionality.
/// </summary>
public sealed class Database(IOptions<CdnOptions> options) : BaseScopedDatabase
{
    protected override string GetConnectionString()
    {
        return GetConnectionStringForFile(options.Value.DatabaseFileName);
    }
}
