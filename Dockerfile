# syntax=docker/dockerfile:1

# ---- build + test -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from the project files alone so the NuGet layer stays cached until a .csproj changes.
COPY global.json Directory.Build.props QuizBot.sln ./
COPY src/QuizBot/QuizBot.csproj src/QuizBot/
COPY tests/QuizBot.Tests/QuizBot.Tests.csproj tests/QuizBot.Tests/
RUN dotnet restore QuizBot.sln

COPY . .

# The image only builds when every test passes, including the question-bank content tests.
RUN dotnet test --solution QuizBot.sln -c Release --no-restore
RUN dotnet publish src/QuizBot/QuizBot.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Workstation GC. Server GC creates one heap (and one GC thread) per core and sizes its budgets for throughput,
# so under a 256 MB container limit it reserves memory the bot never needs and can push the process into the
# OOM killer. A mostly idle, single-user bot wants the small footprint of Workstation (concurrent) GC.
ENV DOTNET_gcServer=0 \
    Quiz__QuestionsDir=/questions \
    Quiz__DbPath=/data/quizbot.db \
    Quiz__HeartbeatPath=/tmp/quizbot.heartbeat

COPY --from=build /app/publish .

# /data must be writable by the non-root user. This covers a named volume; for the ./data bind mount in
# docker-compose.yml the host directory's ownership wins, which the quizbot-data-init service fixes.
RUN mkdir -p /data && chown app:app /data
USER app

# The scheduler touches the heartbeat file after every successful 30 s tick. Two minutes without a touch means
# the ticker is hung or failing on every tick.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["/bin/sh", "-c", "test -n \"$(find \"$Quiz__HeartbeatPath\" -mmin -2 2>/dev/null)\""]

# Exec form: dotnet runs as PID 1 and receives SIGTERM directly, so the host's graceful shutdown runs.
ENTRYPOINT ["dotnet", "QuizBot.dll"]
