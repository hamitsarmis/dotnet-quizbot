using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QuizBot.Persistence;
using QuizBot.Telegram;

namespace QuizBot.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void TheProductionGraphPassesScopeAndBuildValidation()
    {
        using var provider = BuildProductionLikeServices().BuildServiceProvider(Strict);

        // Resolving does not touch the network: TelegramBotClient only connects on the first API call.
        Assert.IsType<TelegramMessenger>(provider.GetRequiredService<IQuizMessenger>());
        Assert.Equal(3, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void ValidateScopesRejectsACaptiveDependency()
    {
        // The mistake this design avoids: a singleton (every hosted service is one) that takes the scoped
        // QuizDbContext in its constructor. ValidateOnBuild turns it into a startup failure.
        var services = BuildProductionLikeServices();
        services.AddSingleton<CaptiveHolder>();

        var ex = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(Strict));

        Assert.Contains("Cannot consume scoped service", ex.Message);
    }

    private static readonly ServiceProviderOptions Strict = new() { ValidateScopes = true, ValidateOnBuild = true };

    private static ServiceCollection BuildProductionLikeServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = "123456:ABC-def",
                ["Telegram:AllowedChatId"] = "42",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Random.Shared);
        services.AddQuizBot();
        services.AddTelegramMessenger();
        return services;
    }

    private sealed class CaptiveHolder(QuizDbContext db)
    {
        public QuizDbContext Db { get; } = db;
    }
}
