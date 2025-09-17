using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aura.Orchestrator.Configuration;
using Aura.Orchestrator.Metrics;
using Aura.Orchestrator.Models;
using Aura.Orchestrator.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Aura.Orchestrator.Communication;

public sealed class DroneHub : Hub
{
    private readonly IDroneRegistry _droneRegistry;
    private readonly ICommandDispatcher _commandDispatcher;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly ISharedContextStore _sharedContextStore;
    private readonly ILogger<DroneHub> _logger;
    private readonly IMetricsCollector _metrics;
    private readonly OrchestratorConfig _config;

    public DroneHub(
        IDroneRegistry droneRegistry,
        ICommandDispatcher commandDispatcher,
        ISessionRegistry sessionRegistry,
        ISharedContextStore sharedContextStore,
        IOptions<OrchestratorConfig> options,
        ILogger<DroneHub> logger,
        IMetricsCollector metrics)
    {
        _droneRegistry = droneRegistry;
        _commandDispatcher = commandDispatcher;
        _sessionRegistry = sessionRegistry;
        _sharedContextStore = sharedContextStore;
        _logger = logger;
        _metrics = metrics;
        _config = options.Value;
    }

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;
        var apiKey = Context.GetHttpContext()?.Request.Headers["X-API-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey) || !string.Equals(apiKey, _config.Server.ApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Unauthorized connection attempt from {ConnectionId}", connectionId);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
        _logger.LogInformation("Drone connection established: {ConnectionId}", connectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (!string.IsNullOrEmpty(droneId))
        {
            _logger.LogInformation("Drone {DroneId} disconnected: {Reason}", droneId, exception?.Message ?? "normal");
            await _droneRegistry.UnregisterDroneAsync(droneId).ConfigureAwait(false);
            await _commandDispatcher.HandleDroneDisconnectedAsync(droneId).ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public async Task RegisterDrone(DroneRegistrationPayload payload)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(payload.DroneId) || string.IsNullOrWhiteSpace(payload.Version))
        {
            throw new ArgumentException("DroneId and version are required.");
        }

        var drone = new DroneInfo
        {
            DroneId = payload.DroneId,
            ConnectionId = Context.ConnectionId,
            Version = payload.Version,
            StaticCapabilities = payload.StaticCapabilities ?? Array.Empty<string>(),
            Modules = payload.Modules ?? Array.Empty<string>(),
            LimitsSupported = payload.LimitsSupported ?? new LimitsSupport(),
            RegisteredAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow,
            Status = DroneStatus.Idle
        };

        await _droneRegistry.RegisterDroneAsync(drone).ConfigureAwait(false);
        await Groups.AddToGroupAsync(Context.ConnectionId, "drones").ConfigureAwait(false);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"drone_{payload.DroneId}").ConfigureAwait(false);

        _metrics.IncrementCounter("drones_registered_total");
        _logger.LogInformation("Drone {DroneId} registered successfully", payload.DroneId);
    }

    public async Task AcknowledgeCommand(string commandId)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (droneId == null)
        {
            _logger.LogWarning("Received acknowledgment from unknown connection {ConnectionId}", Context.ConnectionId);
            return;
        }

        await _commandDispatcher.HandleAcknowledgmentAsync(commandId, droneId).ConfigureAwait(false);
    }

    public async Task ReportResult(CommandResultPayload payload)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (droneId == null)
        {
            _logger.LogWarning("Result reported from unknown connection {ConnectionId}", Context.ConnectionId);
            return;
        }

        await PersistArtifactsAsync(payload).ConfigureAwait(false);
        await _commandDispatcher.HandleResultAsync(payload.CommandId, droneId, payload).ConfigureAwait(false);
    }

    public async Task ReportError(CommandErrorPayload payload)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (droneId == null)
        {
            _logger.LogWarning("Error reported from unknown connection {ConnectionId}", Context.ConnectionId);
            return;
        }

        await _commandDispatcher.HandleErrorAsync(payload.CommandId, droneId, payload).ConfigureAwait(false);
    }

    public async Task ReportStatus(StatusPayload payload)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (droneId == null)
        {
            return;
        }

        await _droneRegistry.UpdateDroneStatusAsync(droneId, payload).ConfigureAwait(false);
        await _droneRegistry.UpdateHeartbeatAsync(droneId).ConfigureAwait(false);
    }

    public async Task RequireIntervention(InterventionPayload payload)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (droneId == null)
        {
            return;
        }

        await _commandDispatcher.HandleInterventionRequestAsync(payload.CommandId, droneId, payload).ConfigureAwait(false);
    }

    public async Task QueryResponse(QueryResponsePayload payload)
    {
        var droneId = _droneRegistry.GetDroneIdByConnection(Context.ConnectionId);
        if (droneId == null)
        {
            return;
        }

        await _commandDispatcher.HandleQueryResponseAsync(payload.QueryId, droneId, payload).ConfigureAwait(false);
    }

    private async Task PersistArtifactsAsync(CommandResultPayload payload)
    {
        if (payload.Artifacts != null)
        {
            foreach (var artifact in payload.Artifacts)
            {
                try
                {
                    switch (artifact.Type)
                    {
                        case "facts":
                            if (artifact.Data is JArray facts)
                            {
                                await _sharedContextStore.StoreFactsAsync(facts).ConfigureAwait(false);
                            }
                            break;
                        case "snippets":
                            if (artifact.Data is JArray snippets)
                            {
                                await _sharedContextStore.StoreSnippetsAsync(snippets).ConfigureAwait(false);
                            }
                            break;
                        default:
                            await _sharedContextStore.StoreArtifactAsync(artifact).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    var artifactType = string.IsNullOrWhiteSpace(artifact.Type) ? "unknown" : artifact.Type;
                    _logger.LogError(
                        ex,
                        "Failed to persist artifact {ArtifactType} for command {CommandId}",
                        artifactType,
                        payload.CommandId);
                    _metrics.IncrementCounter(
                        "artifacts_persist_failed_total",
                        1,
                        new Dictionary<string, string> { ["type"] = artifactType });
                }
            }
        }

        if (!string.IsNullOrEmpty(payload.SessionLeaseId) && payload.SessionState is JObject state)
        {
            try
            {
                await _sessionRegistry.UpdateSessionStateAsync(payload.SessionLeaseId, state).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to persist session state for lease {LeaseId} from command {CommandId}",
                    payload.SessionLeaseId,
                    payload.CommandId);
                _metrics.IncrementCounter("session_state_persist_failed_total");
            }
        }
    }
}

public static class DroneHubExtensions
{
    public static Task SendCommandToDroneAsync(this IHubContext<DroneHub> hubContext, string droneId, CommandPayload payload, CancellationToken cancellationToken = default)
    {
        return hubContext.Clients.Group($"drone_{droneId}").SendAsync("ExecuteCommand", payload, cancellationToken);
    }

    public static Task SendQueryToDroneAsync(this IHubContext<DroneHub> hubContext, string droneId, QueryPayload payload, CancellationToken cancellationToken = default)
    {
        return hubContext.Clients.Group($"drone_{droneId}").SendAsync("ExecuteQuery", payload, cancellationToken);
    }
}
