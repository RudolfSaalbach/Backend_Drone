using System;
using System.Threading;
using System.Threading.Tasks;
using Aura.Orchestrator.Communication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Aura.Orchestrator.Interventions;

public sealed class SignalRInterventionEventPublisher : IInterventionEventPublisher
{
    private readonly IHubContext<DroneHub> _hubContext;
    private readonly ILogger<SignalRInterventionEventPublisher> _logger;

    public SignalRInterventionEventPublisher(
        IHubContext<DroneHub> hubContext,
        ILogger<SignalRInterventionEventPublisher> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task PublishAsync(InterventionRequiredEvent @event, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Broadcasting intervention event for command {CommandId}", @event.CommandId);
        return _hubContext
            .Clients
            .Group("operators")
            .SendAsync("RequireIntervention", @event, cancellationToken);
    }
}
