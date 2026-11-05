using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuizBot.Options;

namespace QuizBot.Tests;

public sealed class OptionsTests
{
    private static readonly Dictionary<string, string?> ValidTelegram = new()
    {
        ["Telegram:BotToken"] = "123456:ABC-def_ghi",
        ["Telegram:AllowedChatId"] = "42",
    };

    [Fact]
    public void DefaultsMatchTheSpec()
    {
        var quiz = Resolve<QuizOptions>(ValidTelegram);

        Assert.Equal("Europe/Istanbul", quiz.TimeZone);
        Assert.Equal(new TimeOnly(9, 0), quiz.WindowStart);
        Assert.Equal(new TimeOnly(22, 0), quiz.WindowEnd);
        Assert.Equal(4, quiz.QuestionsPerDay);
        Assert.Equal(60, quiz.MinGapMinutes);
        Assert.Equal("/questions", quiz.QuestionsDir);
        Assert.Equal("/data/quizbot.db", quiz.DbPath);
    }

    [Fact]
    public void DoubleUnderscoreEnvironmentVariablesBindToSections()
    {
        // A unique prefix keeps this test from touching variables other tests could see.
        const string prefix = "QUIZBOT_OPTIONS_TEST_";
        Environment.SetEnvironmentVariable(prefix + "Quiz__WindowStart", "08:30");
        Environment.SetEnvironmentVariable(prefix + "Quiz__QuestionsPerDay", "6");
        Environment.SetEnvironmentVariable(prefix + "Telegram__AllowedChatId", "777");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(ValidTelegram)
                .AddEnvironmentVariables(prefix)
                .Build();

            var quiz = Resolve<QuizOptions>(configuration);
            var telegram = Resolve<TelegramOptions>(configuration);

            Assert.Equal(new TimeOnly(8, 30), quiz.WindowStart);
            Assert.Equal(6, quiz.QuestionsPerDay);
            Assert.Equal(777, telegram.AllowedChatId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Quiz__WindowStart", null);
            Environment.SetEnvironmentVariable(prefix + "Quiz__QuestionsPerDay", null);
            Environment.SetEnvironmentVariable(prefix + "Telegram__AllowedChatId", null);
        }
    }

    [Theory]
    [InlineData("Quiz:WindowEnd", "08:00", "must be after window start")]
    [InlineData("Quiz:TimeZone", "Mars/Olympus_Mons", "tzdata")]
    [InlineData("Quiz:QuestionsPerDay", "0", "QuestionsPerDay")]
    [InlineData("Quiz:MinGapMinutes", "400", "do not fit")]
    public void InvalidQuizSettingsFailWithAClearMessage(string key, string value, string expectedMessage)
    {
        var ex = Assert.Throws<OptionsValidationException>(() =>
            Resolve<QuizOptions>(new Dictionary<string, string?>(ValidTelegram) { [key] = value }));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void MissingTelegramSettingsFail()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve<TelegramOptions>(new Dictionary<string, string?>()));

        Assert.Contains("Telegram__BotToken", ex.Message);
        Assert.Contains("Telegram__AllowedChatId", ex.Message);
    }

    [Fact]
    public void AMalformedTokenIsRejectedWithoutEchoingIt()
    {
        const string leakedLookingToken = "not-a-token-but-still-secret";

        var ex = Assert.Throws<OptionsValidationException>(() =>
            Resolve<TelegramOptions>(new Dictionary<string, string?>(ValidTelegram) { ["Telegram:BotToken"] = leakedLookingToken }));

        Assert.Contains("does not look like a bot token", ex.Message);
        Assert.DoesNotContain(leakedLookingToken, ex.Message);
    }

    [Fact]
    public void TelegramOptionsToStringHidesTheToken()
    {
        var options = new TelegramOptions { BotToken = "123:secret", AllowedChatId = 1 };

        Assert.DoesNotContain("secret", options.ToString());
    }

    private static T Resolve<T>(Dictionary<string, string?> settings) where T : class =>
        Resolve<T>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    private static T Resolve<T>(IConfiguration configuration) where T : class
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddQuizBot();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }
}
