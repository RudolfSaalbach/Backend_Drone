using System;
using System.Threading;
using System.Threading.Tasks;
using Aura.Orchestrator.Communication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Aura.Orchestrator.Interventions;

public sealed class SignalROperatorNotifier : IInterventionNotificationSink
{
    private readonly IHubContext<DroneHub> _hubContext;
    private readonly ILogger<SignalROperatorNotifier> _logger;

    public SignalROperatorNotifier(IHubContext<DroneHub> hubContext, ILogger<SignalROperatorNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyAsync(InterventionNotification notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await _hubContext.Clients.Group("operators").SendAsync(
                "InterventionRequested",
                new
                {
                    commandId = notification.CommandId,
                    droneId = notification.DroneId,
                    type = notification.Type ?? "unknown",
                    reason = notification.Reason ?? "unspecified",
                    requestedAtUtc = notification.RequestedAtUtc,
                    metadata = notification.Metadata
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Skipping operator notification for command {CommandId} due to cancellation",
                notification.CommandId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to notify operators about intervention for command {CommandId}",
                notification.CommandId);
            throw;
        }
    }
}
