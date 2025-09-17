using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aura.Orchestrator.Interventions;

public interface IInterventionEventPublisher
{
    Task PublishAsync(InterventionRequiredEvent @event, CancellationToken cancellationToken = default);
}

public sealed record InterventionRequiredEvent(
    string CommandId,
    string ParentCommandId,
    string Reason,
    bool Resumable,
    string? Url,
    IReadOnlyDictionary<string, object?> Context,
    string? ScreenshotPath);
