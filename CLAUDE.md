# CLAUDE.md

- Read `SPEC.md` first; it holds every decision. If something is ambiguous, ask once, then proceed.
- .NET 10, C# 14, nullable enabled, warnings as errors. One Worker Service project plus one xUnit test project.
- All time logic goes through `TimeProvider`; all randomness through an injected `Random`. No `DateTime.Now`/`UtcNow` outside `Program.cs`.
- Scoped services are resolved via `IServiceScopeFactory` inside background loops; never inject `QuizDbContext` into a `BackgroundService`.
- No references to `Telegram.Bot` outside `src/QuizBot/Telegram/`.
- Tests: xUnit + `FakeTimeProvider`; fakes over mocks (NSubstitute only if unavoidable). Run `dotnet test` before every commit.
- Comments explain *why*, especially where a choice illustrates an interview topic (captive dependency, at-least-once delivery, GC mode in containers, options validation at startup).
- Conventional commits; one small, reviewable commit per SPEC step.
- Never commit `.env`; the bot token must not appear in logs.
