using Microsoft.Extensions.DependencyInjection.Extensions;
using QuizBot;
using QuizBot.Telegram;

// Content root = the app directory, so appsettings.json is found no matter where the process starts from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Scope validation is on in every environment, not only Development: a captive dependency (a singleton
// holding a scoped QuizDbContext) then fails at startup instead of corrupting state in production.
// This is HostApplicationBuilder's equivalent of IHostBuilder.UseDefaultServiceProvider.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true,
}));

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss 'UTC' ";
});

// Shorter than docker-compose's stop_grace_period (30 s), so the host finishes its own shutdown before
// Docker escalates SIGTERM to SIGKILL.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

// The only place real time and real randomness enter the app; everything else takes them by injection.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddSingleton(Random.Shared);

builder.Services.AddQuizBot();
builder.Services.AddTelegramMessenger();

var host = builder.Build();
await host.RunAsync();
