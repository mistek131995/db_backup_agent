using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Workers.Handlers;

namespace BackupsterAgent.Workers;

public sealed class AgentTaskPollingService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IAgentTaskClient _client;
    private readonly IReadOnlyList<IAgentTaskHandler> _handlers;
    private readonly IAgentActivityLock _activityLock;
    private readonly ILogger<AgentTaskPollingService> _logger;

    public AgentTaskPollingService(
        IAgentTaskClient client,
        IEnumerable<IAgentTaskHandler> handlers,
        IAgentActivityLock activityLock,
        ILogger<AgentTaskPollingService> logger)
    {
        _client = client;
        _handlers = handlers.ToArray();
        _activityLock = activityLock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AgentTaskPollingService started. Poll interval: {PollSec}s, initial backoff: {BackoffSec}s, max backoff: {MaxSec}s",
            PollInterval.TotalSeconds, InitialBackoff.TotalSeconds, MaxBackoff.TotalSeconds);

        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _client.FetchTaskAsync(stoppingToken);
                backoff = InitialBackoff;

                if (task is null)
                {
                    if (!await DelayOrCancel(PollInterval, stoppingToken)) break;
                    continue;
                }

                bool cancelled = false;
                using (await _activityLock.AcquireAsync($"task:{task.Type}:{task.Id}", stoppingToken))
                {
                    PatchAgentTaskDto patch;
                    try
                    {
                        patch = await DispatchAsync(task, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "AgentTaskPollingService: task {TaskId} cancelled mid-pipeline", task.Id);
                        cancelled = true;
                        patch = new PatchAgentTaskDto
                        {
                            Status = AgentTaskStatus.Failed,
                            ErrorMessage = "Задача прервана: агент остановлен.",
                            Restore = task.Type == AgentTaskType.Restore
                                ? new RestoreTaskResult { DatabaseStatus = RestoreDatabaseStatus.Failed }
                                : null,
                        };
                    }

                    using var finalizeCts = cancelled ? new CancellationTokenSource(TimeSpan.FromSeconds(10)) : null;
                    var patchCt = cancelled ? finalizeCts!.Token : stoppingToken;

                    try
                    {
                        await _client.PatchTaskAsync(task.Id, patch, patchCt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "AgentTaskPollingService: failed to PATCH final status for task {TaskId}. " +
                            "Task will be marked in_progress until sweeper picks it up.", task.Id);
                    }
                }

                if (cancelled) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "AgentTaskPollingService: dashboard unavailable ({ErrorType}: {ErrorMessage}), backing off {BackoffSec}s",
                    ex.GetType().Name, ex.Message, backoff.TotalSeconds);

                if (!await DelayOrCancel(backoff, stoppingToken)) break;

                var next = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
                backoff = next;
            }
        }

        _logger.LogInformation("AgentTaskPollingService stopped");
    }

    private Task<PatchAgentTaskDto> DispatchAsync(AgentTaskForAgentDto task, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(task))
                return handler.HandleAsync(task, ct);
        }

        return Task.FromResult(RejectUnsupported(task, task.Type.ToString()));
    }

    private PatchAgentTaskDto RejectUnsupported(AgentTaskForAgentDto task, string typeName)
    {
        _logger.LogWarning(
            "AgentTaskPollingService: task {TaskId} has unsupported type '{Type}'. " +
            "Update the agent to a version that supports this task type.",
            task.Id, typeName);
        return new PatchAgentTaskDto
        {
            Status = AgentTaskStatus.Failed,
            ErrorMessage =
                $"Тип задачи '{typeName}' не поддерживается этой версией агента. " +
                "Обновите агента до актуальной версии.",
        };
    }

    private static async Task<bool> DelayOrCancel(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
