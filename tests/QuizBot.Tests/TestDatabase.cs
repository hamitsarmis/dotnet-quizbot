using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuizBot.Persistence;

namespace QuizBot.Tests;

/// <summary>
/// A private in-memory SQLite database with the real migrations applied. The connection stays open for the
/// lifetime of the instance because an in-memory SQLite database disappears when its last connection closes.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        Options = new DbContextOptionsBuilder<QuizDbContext>().UseSqlite(Connection).Options;

        using var db = CreateContext();
        db.Database.Migrate();
    }

    public SqliteConnection Connection { get; }

    public DbContextOptions<QuizDbContext> Options { get; }

    public QuizDbContext CreateContext() => new(Options);

    public void Dispose() => Connection.Dispose();
}
