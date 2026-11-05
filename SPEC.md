# QuizBot — Senior .NET Interview Drill Bot

## 1. Purpose

A single-user Telegram bot that drills Hamit on senior .NET interview questions. It sends a random question a few times a day inside a local time window, reveals a model answer on demand, lets him self-grade, and biases future questions toward what he got wrong. It runs as one Docker container on his VPS.

Secondary purpose, equally important: **the bot itself is study material.** It is deliberately a .NET 10 Worker Service so that building it exercises the topics the questions cover — `BackgroundService`, DI scopes in a long-running process, `TimeProvider`, EF Core with SQLite and migrations, cancellation, graceful shutdown, Docker and GC settings in a small container. Where there is a choice between the shortest implementation and the one that teaches the interview concept, pick the latter and leave a short comment saying why.

## 2. Decisions already made (do not relitigate)

- Runtime: **.NET 10** (LTS), C# 14, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- Host: **Worker Service** (`Microsoft.Extensions.Hosting`, `Host.CreateApplicationBuilder`). No ASP.NET Core web host in v1.
- Enable `ValidateScopes` and `ValidateOnBuild` in **all** environments (`UseDefaultServiceProvider`), not just Development.
- Telegram: **`Telegram.Bot`** NuGet (latest stable), **long polling** — no public endpoint, no webhook, the VPS firewall stays closed. Wrap it behind our own `IQuizMessenger` interface; nothing outside `src/QuizBot/Telegram/` references `Telegram.Bot` types.
- Persistence: **EF Core 10 + SQLite** (`Microsoft.EntityFrameworkCore.Sqlite`). Migrations committed to the repo and applied with `Database.Migrate()` at startup. Acceptable because this is a single-instance app — add a comment explaining why this would be wrong for a multi-instance API (migration race, long locks during deploy).
- Time: all scheduling logic goes through **`TimeProvider`** (`TimeProvider.System` in production, `FakeTimeProvider` in tests). No `DateTime.Now`/`DateTime.UtcNow` outside `Program.cs`. Time zone comes from config and is resolved with `TimeZoneInfo.FindSystemTimeZoneById`.
- Randomness: an injected `Random` instance (seeded in tests).
- Configuration: options pattern with `IOptions<T>`, bound from environment variables using the double-underscore convention (`Quiz__WindowStart`), validated with `ValidateDataAnnotations().ValidateOnStart()`.
- Tests: **xUnit**, `Microsoft.Extensions.TimeProvider.Testing`, fakes over mocks; if a mocking library is unavoidable, **NSubstitute**. Never mock `ITelegramBotClient`; fake `IQuizMessenger` instead.
- Logging: `Microsoft.Extensions.Logging` to console; `[LoggerMessage]` source-generated methods for the hot paths (ticker, send, command).
- Container: multi-stage Dockerfile, non-root user, Workstation GC (`DOTNET_gcServer=0`) because the container runs with a small memory limit — comment on why Server GC would be the wrong default there.
- Language of code, comments, commits and bot messages: **English**.

## 3. Functional requirements

### 3.1 Scheduled questions
- Config (with defaults): `Quiz__TimeZone=Europe/Istanbul`, `Quiz__WindowStart=09:00`, `Quiz__WindowEnd=22:00`, `Quiz__QuestionsPerDay=4`, `Quiz__MinGapMinutes=60`.
- Each local day gets a **persisted** schedule of `QuestionsPerDay` slots. Generation: split the window into N equal buckets and pick a uniformly random time inside each bucket, then enforce `MinGapMinutes` between consecutive slots by nudging later slots forward (never past the window end). This gives spread without rejection sampling.
- A day's schedule is generated when first needed: at startup if today has none, and by a daily check shortly after local midnight. Slots already in the past at generation time are stored as `Skipped` — **never send a backlog** after downtime.
- A ticker (`BackgroundService` + `PeriodicTimer`, 30 s) sends every `Pending` slot whose fire time has passed. Set `SentAtUtc` after a successful send (at-least-once; the 30 s tick makes the duplicate window negligible — document the trade-off in a comment). After 3 failed attempts mark the slot `Failed` and log an error.
- Each scheduled send creates an `AskedQuestions` row with `Source = Scheduled`.
- While paused (`/pause`), the ticker marks due slots `Skipped` instead of sending.

### 3.2 Commands
- `/start` — confirms the bot is alive, prints today's remaining slot times (local) and a short help text.
- `/ask [topic]` — sends a question immediately; does **not** consume a scheduled slot. Optional topic filter (exact, case-insensitive). Unknown topic → list the topics.
- `/answer` — reveals the model answer and follow-ups for the most recent asked question that has not been revealed yet; if everything is revealed, re-reveals the most recent one. After the answer, show an inline keyboard: **Knew it / Partially / Didn't know**. Recording a grade edits the message to show the chosen grade and removes the keyboard. Stale or duplicate callbacks are acknowledged (`answerCallbackQuery`) and ignored.
- `/skip` — marks the current open question as skipped.
- `/topics` — topics with question counts and pass rate per topic.
- `/stats` — asked / revealed / grade distribution overall and for the last 7 days; current streak of days with at least one grade.
- `/pause`, `/resume` — pause scheduled sends; manual `/ask` still works.
- Unknown command or free text → one-line help.

### 3.3 Question selection
- Candidate pool: active questions, optionally filtered by topic, excluding questions asked in the last 3 days unless that would empty the pool.
- Weight per question from its most recent outcome: never asked → 3.0; `DidntKnow` → 2.5; skipped → 1.5; `Partially` → 1.5; `KnewIt` once → 1.0; `KnewIt` twice in a row → 0.5. Pick by weighted random.
- The algorithm lives in a pure class (`QuestionSelector`) with no I/O, unit-tested with a seeded `Random`.

### 3.4 Single-user security
- `Telegram__BotToken` and `Telegram__AllowedChatId` come from environment variables (`.env`, git-ignored; `.env.example` committed).
- Updates from any other chat are ignored and logged at Warning once per chat id per process lifetime. The bot never replies to unknown chats.

## 4. Data

### 4.1 Question files (`questions/*.json`)
Each file is a JSON array of objects:

```json
{
  "id": "async-002",
  "topic": "async",
  "difficulty": "core | senior | deep",
  "question": "...",
  "answer": "...",
  "followups": ["...", "..."],
  "tags": ["deadlock", "wpf"]
}
```

- `id` is unique across all files; `topic` is a lowercase slug; `answer` is plain text that may contain backticks for code. Render backticks as `<code>` in Telegram HTML parse mode and HTML-escape everything else.
- Loaded at startup from `Quiz__QuestionsDir` (default `/questions`): validate, then upsert by `id` into `Questions`. Questions missing from the files are marked `IsActive = false` — never deleted, history references them. Loading is idempotent: edit an answer, restart the container, the stored question updates.

### 4.2 Database (SQLite at `Quiz__DbPath`, default `/data/quizbot.db`)
- `Questions(Id text PK, Topic, Difficulty, Question, Answer, FollowupsJson, TagsJson, IsActive, UpdatedAtUtc)`
- `ScheduledSlots(Id int PK, LocalDate text, FireAtUtc, Status {Pending, Sent, Skipped, Failed}, Attempts int, SentAtUtc?, AskedQuestionId?)`
- `AskedQuestions(Id int PK, QuestionId FK, AskedAtUtc, Source {Scheduled, Manual}, TelegramMessageId long, RevealedAtUtc?, Grade? {KnewIt, Partially, DidntKnow}, GradedAtUtc?, SkippedAtUtc?)`
- `BotState(Key text PK, Value text)` — e.g. `paused`.
- Indexes: `AskedQuestions(QuestionId, AskedAtUtc)`, `ScheduledSlots(Status, FireAtUtc)`.

## 5. Non-functional
- Restart safety: schedules and asked questions survive restarts; no burst of missed questions after downtime.
- Shutdown: honour the stopping token in every loop; set `HostOptions.ShutdownTimeout` explicitly and keep Docker's `stop_grace_period` longer than it.
- Startup log line: resolved time zone, current local time, today's slot times, number of active questions. If the time zone cannot be resolved (missing tzdata), fail fast with a clear message.
- Message length: split anything over 4000 characters at a paragraph boundary (Telegram limit is 4096).
- No PII and no secrets in logs; the bot token must never appear in logs or exception messages.

## 6. Project layout

```
dotnet-quizbot/
  SPEC.md  CLAUDE.md  README.md  .env.example  .gitignore
  Dockerfile  docker-compose.yml  QuizBot.sln
  questions/*.json
  src/QuizBot/
    Program.cs                               # composition root, options binding + ValidateOnStart
    Options/QuizOptions.cs  Options/TelegramOptions.cs
    Questions/QuestionLoader.cs  QuestionSelector.cs  QuestionFormatter.cs
    Scheduling/ScheduleGenerator.cs (pure)  SchedulerService.cs (BackgroundService)
    Telegram/IQuizMessenger.cs  TelegramMessenger.cs  UpdateHandlerService.cs (BackgroundService)  Commands/*.cs
    Persistence/QuizDbContext.cs  Persistence/Migrations/
  tests/QuizBot.Tests/
    ScheduleGeneratorTests.cs  QuestionSelectorTests.cs  QuestionContentTests.cs
    CommandHandlingTests.cs  SchedulerServiceTests.cs
```

Scoped services (`QuizDbContext`) are resolved inside a scope created per tick / per update via `IServiceScopeFactory` — never injected into a `BackgroundService` constructor. This is the captive-dependency lesson; keep the comment that says so.

## 7. Testing requirements
- `ScheduleGenerator`: exactly N slots inside the window; respects the minimum gap; deterministic for a seeded `Random`; slots before "now" are `Skipped` when generated mid-day; window end before start → clear exception.
- `QuestionSelector`: weight function matches §3.3; excludes recently asked unless the pool would be empty; topic filter works; a statistical check with a fixed seed shows `DidntKnow` questions are drawn more often than `KnewIt` ones over 10k draws.
- `QuestionContentTests`: every `questions/*.json` parses; ids unique across files; required fields non-empty; `answer` ≤ 3500 chars; at least one question per topic.
- `SchedulerServiceTests`: with `FakeTimeProvider` and a fake `IQuizMessenger`, advancing time past a slot sends exactly one message; a failed send increments `Attempts` and retries next tick; nothing is sent while paused.
- Command handling: `/answer` with no open question returns the help text; a grade callback for an already-graded question is ignored.
- `dotnet test` runs inside the Dockerfile build stage and must pass.

## 8. Deployment
- `Dockerfile`: `mcr.microsoft.com/dotnet/sdk:10.0` build + test stage → `mcr.microsoft.com/dotnet/runtime:10.0` final; `USER app`; `ENV DOTNET_gcServer=0`; `HEALTHCHECK` based on a heartbeat file the ticker touches every 30 s.
- `docker-compose.yml`: service `quizbot`, `restart: unless-stopped`, `env_file: .env`, volumes `./data:/data` and `./questions:/questions:ro`, `mem_limit: 256m`, `stop_grace_period: 30s`.
- Target: the existing Ubuntu VPS. `docker compose up -d --build` is the whole deployment.
- `README.md`: creating the bot with BotFather, finding the chat id, first run, editing questions.

## 9. Acceptance criteria
- [ ] `docker compose up -d` on a clean machine with a valid `.env` starts the bot; `/start` answers within 5 s.
- [ ] Every day exactly N questions arrive between 09:00 and 22:00 Istanbul time with ≥ 60 min gaps; none outside the window; none as a burst after the container was down.
- [ ] `/ask`, `/ask gc`, `/answer`, `/skip`, `/topics`, `/stats`, `/pause`, `/resume` behave as in §3.2.
- [ ] Grading via inline buttons is persisted and changes selection weights.
- [ ] Editing a question file and restarting updates the stored question; removing one deactivates it.
- [ ] Messages from another chat id get no reply.
- [ ] All tests green; warnings as errors; no `DateTime.Now`/`UtcNow` outside `Program.cs`.
- [ ] Startup log shows time zone, local time and today's schedule.

## 10. Out of scope for v1 (v2 ideas, in priority order)
1. **Typed or voice answers graded by an LLM** (Claude API) against the model answer, with feedback on precision of language and completeness of the causal chain — the most valuable extension by far, given that articulation is the weak point.
2. Proper spaced repetition (SM-2 intervals) instead of weighted random.
3. A minimal-API dashboard (`/healthz`, `/stats`) — also a good ASP.NET Core exercise.
4. Multi-user support, webhooks, Markdown export of stats.

## 11. Optional: learning mode
If Hamit asks for learning mode, implement everything **except** `ScheduleGenerator`, `QuestionSelector` and the scope handling inside `SchedulerService`. Leave those with signatures, XML docs and failing tests, and let him write them. Otherwise implement everything.

## 12. Kick-off prompt for Claude Code
> Read SPEC.md and CLAUDE.md. Create the solution skeleton described in §6, then implement in this order, running `dotnet test` after each step: (1) question loading + QuestionContentTests, (2) ScheduleGenerator + tests, (3) QuestionSelector + tests, (4) persistence + migrations, (5) SchedulerService and UpdateHandlerService against a fake messenger + tests, (6) Telegram adapter and commands, (7) Dockerfile, compose, README. Ask before adding any NuGet package not listed in §2. Commit after each step.
