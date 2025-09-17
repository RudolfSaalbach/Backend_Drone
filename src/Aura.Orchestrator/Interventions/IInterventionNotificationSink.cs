using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aura.Orchestrator.Interventions;

public sealed record InterventionNotification(
    string CommandId,
    string DroneId,
    string? Type,
    string? Reason,
    DateTime RequestedAtUtc,
    IReadOnlyDictionary<string, object?>? Metadata);

public interface IInterventionNotificationSink
{
    Task NotifyAsync(InterventionNotification notification, CancellationToken cancellationToken = default);
}
