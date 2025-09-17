using System.Threading;
using Aura.Orchestrator.Communication;

namespace Aura.Orchestrator.Services;

public interface ICommandDispatcher
{
    Task HandleAcknowledgmentAsync(string commandId, string droneId, CancellationToken cancellationToken = default);

    Task HandleResultAsync(string commandId, string droneId, CommandResultPayload payload, CancellationToken cancellationToken = default);

    Task HandleErrorAsync(string commandId, string droneId, CommandErrorPayload payload, CancellationToken cancellationToken = default);

    Task HandleInterventionRequestAsync(string commandId, string droneId, InterventionPayload payload, CancellationToken cancellationToken = default);

    Task HandleQueryResponseAsync(string queryId, string droneId, QueryResponsePayload payload, CancellationToken cancellationToken = default);

    Task HandleDroneDisconnectedAsync(string droneId, CancellationToken cancellationToken = default);
}
