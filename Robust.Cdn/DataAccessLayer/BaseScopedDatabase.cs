using Dapper;
using Microsoft.Data.Sqlite;

namespace Robust.Cdn.DataAccessLayer;

public abstract class BaseScopedDatabase : IDisposable
{
    private SqliteConnection? _connection;
    public SqliteConnection Connection => _connection ??= OpenConnection();

    private SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection(GetConnectionString());
        con.Open();
        con.Execute("PRAGMA journal_mode=WAL");
        return con;
    }

#pragma warning disable CA1816
    public void Dispose()
    {
        _connection?.Dispose();
    }
#pragma warning restore CA1816

    protected abstract string GetConnectionString();

    protected string GetConnectionStringForFile(string fileName)
    {
        return $"Data Source={fileName};Mode=ReadWriteCreate;Pooling=True;Foreign Keys=True";
    }
}