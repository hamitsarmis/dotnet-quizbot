using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuizBot.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time, so generating a migration does not need the bot's
/// environment variables (token, chat id) or run the host.
/// </summary>
internal sealed class QuizDbContextFactory : IDesignTimeDbContextFactory<QuizDbContext>
{
    public QuizDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<QuizDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
