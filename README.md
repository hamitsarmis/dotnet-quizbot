# dotnet-quizbot

A single-user Telegram bot that drills senior .NET interview questions.

It sends a random question a few times a day inside a local time window, reveals a model answer on `/answer`, and lets you grade yourself. Questions you got wrong come back more often. The bot is a .NET 10 Worker Service with EF Core on SQLite, and it is written to be study material itself: the code exercises the topics the questions ask about. It talks to Telegram by long polling, so it needs no public endpoint.

## Architecture

```
 Telegram Bot API
   ▲          │  getUpdates (long polling, outbound only)
   │          ▼
   │   UpdateHandlerService ──▶ CommandDispatcher ──▶ /start /ask /answer /skip
   │     one DI scope per update                      /topics /stats /pause /resume
   │                                                  grade buttons
   │ sendMessage                                            │
   │                                                        ▼
 SchedulerService ──▶ ScheduleService ──▶ QuestionAsker ──▶ QuestionSelector
   30 s tick,          today's slots       sends, then      weighted by the
   one DI scope        (ScheduleGenerator) records          last outcomes
   per tick                 │                   │
                            ▼                   ▼
                     SQLite via EF Core  (./data/quizbot.db)
                            ▲
 questions/*.json ──▶ QuestionLoader: validate, upsert by id at startup
```

## Components

**`Scheduling/`** — `ScheduleGenerator` splits the daily window into equal buckets, picks one random time per bucket and enforces the minimum gap. `SchedulerService` is the 30-second ticker that plans today and sends every pending slot that is due.

**`Questions/`** — `QuestionLoader` validates the JSON files and syncs them into the database. `QuestionSelector` picks the next question with weights from the most recent outcomes. `QuestionAsker` sends a question and records that it was asked.

**`Telegram/`** — `UpdateHandlerService` long-polls and dispatches each update in its own scope. `Commands/` holds one class per command and the grade-button handler. Nothing outside this folder references `Telegram.Bot`; the rest of the code sees `IQuizMessenger`.

**`Persistence/`** — `QuizDbContext`, entities and migrations. Migrations are applied at startup by `StartupInitializer`.

**`tests/QuizBot.Tests`** — xUnit, 282 tests, with `FakeTimeProvider`, a seeded `Random` and a fake messenger. They also validate every file in `questions/`.

## Quick start

Needs Docker with the Compose plugin, a bot token and your chat id (both explained below).

```sh
git clone https://github.com/hamitsarmis/dotnet-quizbot && cd dotnet-quizbot
cp .env.example .env
# edit .env: Telegram__BotToken and Telegram__AllowedChatId
docker compose up -d --build
docker compose logs -f quizbot
```

The build runs the whole test suite; a failing test (including a malformed question file) fails the build. On startup the log shows the resolved time zone, local time, today's slot times and the number of active questions:

```
QuizBot started. Time zone Europe/Istanbul, local time 2026-11-05 13:26 +03:00, today's slots: 09:12 (skipped), 13:40, 16:06, 21:44; 67 active questions; Workstation GC
```

Send `/start` to the bot. It answers with today's remaining slots and the command list.

`docker compose up -d --build` is also the whole redeploy. The SQLite database lives in `./data` and survives rebuilds. A one-shot `quizbot-data-init` service gives `./data` to the container's non-root user (UID 1654) before the bot starts.

### Create the bot

1. In Telegram, open a chat with [@BotFather](https://t.me/BotFather) and send `/newbot`.
2. Choose a display name and a username ending in `bot`.
3. BotFather replies with a token like `123456789:AAH...`. Treat it like a password.
4. Optional: send `/setcommands` to BotFather, pick the bot, and paste:

   ```
   start - Status, today's schedule and help
   ask - A question now (optionally: /ask gc)
   answer - Reveal the model answer
   skip - Skip the open question
   topics - Topics, counts and pass rates
   stats - Totals, last 7 days and streak
   pause - Pause scheduled questions
   resume - Resume scheduled questions
   ```

### Find your chat id

The bot only talks to one chat, and ignores (and never answers) everyone else.

1. Open a chat with your new bot and send it any message.
2. Before the bot is running, ask Telegram for that message:

   ```sh
   curl -s "https://api.telegram.org/bot<TOKEN>/getUpdates"
   ```

3. Copy `result[0].message.chat.id`. For a private chat this is your user id, a positive number.

If `result` is empty, send the bot another message and retry. Once QuizBot is running it consumes the updates itself, so this only works while it is stopped.

## Configuration

All settings are environment variables in `.env` (double underscore = section separator). Invalid values stop the container at startup with a message saying what is wrong.

| Variable | Purpose | Default |
|---|---|---|
| `Telegram__BotToken` | Token from BotFather. Never logged. | (required) |
| `Telegram__AllowedChatId` | The only chat the bot talks to. | (required) |
| `Quiz__TimeZone` | IANA time zone for the window and "today". | `Europe/Istanbul` |
| `Quiz__WindowStart` / `Quiz__WindowEnd` | Local window for scheduled questions (same day; no wrap past midnight). | `09:00` / `22:00` |
| `Quiz__QuestionsPerDay` | Scheduled questions per day. | `4` |
| `Quiz__MinGapMinutes` | Minimum gap between scheduled questions. | `60` |
| `Quiz__QuestionsDir` | Question files (mounted read-only from `./questions`). | `/questions` |
| `Quiz__DbPath` | SQLite database (mounted from `./data`). | `/data/quizbot.db` |

## Commands

| Command | What it does |
|---|---|
| `/start` | Confirms the bot is alive, lists today's remaining slots and the commands. |
| `/ask [topic]` | A question now; does not use up a scheduled slot. `/ask gc` filters by topic (exact, case-insensitive). |
| `/answer` | Reveals the latest unrevealed question's answer and follow-ups, with **Knew it / Partially / Didn't know** buttons. With nothing unrevealed, shows the latest one again. |
| `/skip` | Skips the latest question if it is still open. |
| `/topics` | Topics with question counts and pass rate (Knew it ÷ graded). |
| `/stats` | Asked, revealed and grades overall and for the last 7 days, plus your streak of days with a grade. |
| `/pause`, `/resume` | Stop or restart scheduled questions. Slots due while paused are skipped, not caught up. `/ask` keeps working. |

## Editing questions

Questions live in `questions/*.json`, one JSON array per file:

```json
{
  "id": "async-002",
  "topic": "async",
  "difficulty": "core",
  "question": "...",
  "answer": "... `code` in backticks renders as code ...",
  "followups": ["...", "..."],
  "tags": ["deadlock", "wpf"]
}
```

Rules, checked at startup and by the tests:

- `id` is unique across all files. `topic` is a lowercase slug (`async`, `aspnetcore`, `distributed-systems`).
- `difficulty` is `core`, `senior` or `deep`.
- `id`, `topic`, `difficulty`, `question` and `answer` are required; `answer` is at most 3500 characters.
- Text is plain: HTML is escaped, and only backtick spans become code.

To apply edits, restart. `./questions` is mounted into the container, so no rebuild is needed:

```sh
docker compose restart quizbot
docker compose logs quizbot | grep "Question bank synced"
```

Loading is an upsert by `id`: edited questions are updated, new ones added, and questions removed from the files are deactivated, never deleted (your history still points at them). If any file is invalid, the bot refuses to start and the log lists every problem; fix them and restart. Running `dotnet test` (or rebuilding the image) catches the same problems before deploying.

## Operations

- **Health:** the scheduler touches `/tmp/quizbot.heartbeat` after every successful 30-second tick; the Docker `HEALTHCHECK` marks the container unhealthy after two minutes without a touch. `docker compose ps` shows the state.
- **Downtime:** no backlog. Slots that passed while the bot was down are marked skipped; a day first planned mid-day stores its past slots as skipped.
- **Delivery:** a scheduled question is sent, then recorded. A crash between the two can duplicate one question (at-least-once), never lose one silently. A failed send is retried on the next two ticks, then marked failed and logged as an error.
- **Shutdown:** the host gets 20 s to stop (`HostOptions.ShutdownTimeout`); Compose waits 30 s (`stop_grace_period`) before SIGKILL.
- **Backups:** copy `./data/quizbot.db*` while the container is stopped, or use `sqlite3 data/quizbot.db ".backup backup.db"` while it runs.
- **Logs:** console, rotated by Docker (3 × 10 MB). Set `Logging__LogLevel__Default=Debug` in `.env` for more detail.
- **Broken IPv6:** .NET tries IPv6 first. On a host where IPv6 routes exist but do not work, polling outside Docker can hang; Docker's default bridge network has no IPv6, so the container is unaffected. Outside Docker, set `DOTNET_SYSTEM_NET_DISABLEIPV6=1`.

## Development

Requires the .NET 10 SDK.

```sh
dotnet test                      # all tests; runs on Microsoft.Testing.Platform (see global.json)
dotnet tool restore              # installs the pinned dotnet-ef
dotnet ef migrations add <Name> --project src/QuizBot --output-dir Persistence/Migrations
```

To run locally outside Docker, point the paths somewhere writable:

```sh
export Telegram__BotToken=... Telegram__AllowedChatId=...
export Quiz__QuestionsDir=$PWD/questions Quiz__DbPath=$PWD/data/quizbot.db Quiz__HeartbeatPath=/tmp/quizbot.heartbeat
dotnet run --project src/QuizBot
```

`PersistenceTests.MigrationsMatchTheModel` fails if an entity changes without a new migration.

## Where the interview topics live

| Topic | Where |
|---|---|
| Captive dependency, scopes in a `BackgroundService` | `Scheduling/SchedulerService.cs` (remarks), `Telegram/UpdateHandlerService.cs`, `CompositionTests.ValidateScopesRejectsACaptiveDependency` |
| `ValidateScopes` / `ValidateOnBuild` in every environment | `Program.cs` |
| Options validation at startup | `QuizBotServiceCollectionExtensions.cs`, `Options/*.cs`, `OptionsTests.cs` |
| `TimeProvider` and `FakeTimeProvider`, `PeriodicTimer` | `Scheduling/SchedulerService.cs`, `SchedulerServiceTests.cs` |
| At-least-once delivery | `SchedulerService.TickAsync` (scheduled sends), `UpdateHandlerService.ExecuteAsync` (update offsets) |
| Poison messages | `UpdateHandlerService.HandleUpdateAsync` |
| Exceptions in `BackgroundService` stopping the host | `SchedulerService.TickSafelyAsync` |
| EF Core + SQLite: value converters, migrations at startup and why not in a multi-instance API | `Persistence/QuizDbContext.cs`, `StartupInitializer.cs` |
| Hosted-service start order | `QuizBotServiceCollectionExtensions.cs`, `StartupInitializer.cs` |
| `HttpClient` lifetime, DNS, secrets in URLs | `Telegram/TelegramServiceCollectionExtensions.cs`, `Telegram/TelegramMessenger.cs` |
| Thread-safe `Random` | `QuizBotServiceCollectionExtensions.cs`, `Program.cs` |
| Workstation vs Server GC in a small container | `Dockerfile` |
| Graceful shutdown | `Program.cs`, `docker-compose.yml`, `SchedulerServiceTests.StopAsyncEndsTheLoopPromptly` |

## Design decisions

- **Long polling, not a webhook.** Why: the bot only makes outbound requests, so there is no public endpoint to secure and the firewall stays closed. Trade-off: exactly one instance may run; a second one would compete for the same updates.
- **Send first, record second.** A scheduled question is sent and then marked as sent. Why: a crash in between repeats one question instead of silently losing it. Trade-off: delivery is at-least-once, so a duplicate is possible.
- **No backlog after downtime.** Slots that passed while the bot was down are marked skipped. Why: a restart should not deliver a burst of old questions. Trade-off: those questions are simply not asked that day.
- **One DI scope per tick and per update.** Hosted services are singletons and the `DbContext` is scoped; injecting it would capture one context for the life of the process. `ValidateScopes` and `ValidateOnBuild` are on in every environment and reject that graph at startup. Trade-off: background code resolves its services through `IServiceScopeFactory`.
- **Weights from the most recent outcome.** Never graded 3.0, didn't know 2.5, partially or skipped 1.5, knew it once 1.0, knew it twice in a row 0.5; questions asked in the last 3 days are left out unless nothing else remains. Trade-off: older history does not count.
- **Time and randomness are injected.** Everything goes through `TimeProvider` and one `Random`. Why: schedules and selection are reproducible in tests. Trade-off: no code outside `Program.cs` may call `DateTime.Now` or `UtcNow`.
- **Migrations at startup.** Why: one instance, one SQLite file, so there is nobody to race with. Trade-off: this would be wrong for an API with several instances; `StartupInitializer` says why.
- **Workstation GC.** The container has a 256 MB limit; Server GC would size its heaps for the host's cores. Set in the `Dockerfile`.

## Caveats

- One user. The bot answers a single chat id and ignores everyone else without replying.
- Grading is self-assessment. The bot does not check what you would have answered.
- The questions and model answers are study notes. They can be incomplete or wrong.
- The scheduling window cannot wrap past midnight.
- Editing a question file needs a restart to take effect.
- Without a valid bot token the container starts and then logs polling errors; there is no offline mode.

## License

MIT, see [LICENSE](LICENSE).
