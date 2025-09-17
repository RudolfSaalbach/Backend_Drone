using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aura.Orchestrator.Communication;
using Aura.Orchestrator.Metrics;
using Microsoft.Extensions.Logging;

namespace Aura.Orchestrator.Services;

public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly ICommandLifecycleTracker _lifecycleTracker;
    private readonly IDroneRegistry _droneRegistry;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        ICommandLifecycleTracker lifecycleTracker,
        IDroneRegistry droneRegistry,
        IMetricsCollector metrics,
        ILogger<CommandDispatcher> logger)
    {
        _lifecycleTracker = lifecycleTracker;
        _droneRegistry = droneRegistry;
        _metrics = metrics;
        _logger = logger;
    }

    public Task HandleAcknowledgmentAsync(string commandId, string droneId, CancellationToken cancellationToken = default)
    {
        _lifecycleTracker.MarkAcknowledged(commandId, droneId);
        _metrics.IncrementCounter("commands_acknowledged_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
        return Task.CompletedTask;
    }

    public async Task HandleResultAsync(string commandId, string droneId, CommandResultPayload payload, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Command {CommandId} completed successfully on drone {DroneId}", commandId, droneId);
        _lifecycleTracker.Complete(commandId, droneId);
        _metrics.IncrementCounter("commands_completed_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
        await _droneRegistry.UpdateDroneStatusAsync(droneId, new StatusPayload
        {
            Status = "idle",
            CurrentCommand = string.Empty
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleErrorAsync(string commandId, string droneId, CommandErrorPayload payload, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Command {CommandId} failed on drone {DroneId}: {Error}", commandId, droneId, payload.Error);
        _lifecycleTracker.Fail(commandId, droneId, payload.Error);
        _metrics.IncrementCounter("commands_failed_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
        await _droneRegistry.IncrementDroneErrorCountAsync(droneId, cancellationToken).ConfigureAwait(false);
        await _droneRegistry.UpdateDroneStatusAsync(droneId, new StatusPayload
        {
            Status = "idle",
            CurrentCommand = string.Empty
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task HandleInterventionRequestAsync(string commandId, string droneId, InterventionPayload payload, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Drone {DroneId} requested intervention for command {CommandId} ({Type})", droneId, commandId, payload.Type);
        _metrics.IncrementCounter("interventions_requested_total", 1, new Dictionary<string, string>
        {
            ["drone_id"] = droneId,
            ["type"] = payload.Type ?? "unknown"
        });
        return Task.CompletedTask;
    }

    public Task HandleQueryResponseAsync(string queryId, string droneId, QueryResponsePayload payload, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Received query response {QueryId} from drone {DroneId}", queryId, droneId);
        _metrics.IncrementCounter("query_responses_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
        return Task.CompletedTask;
    }

    public Task HandleDroneDisconnectedAsync(string droneId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Releasing outstanding commands for disconnected drone {DroneId}", droneId);
        _lifecycleTracker.FailAll(droneId, "drone_disconnected");
        return Task.CompletedTask;
    }
}
