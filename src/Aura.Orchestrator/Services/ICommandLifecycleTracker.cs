using System.Threading;

namespace Aura.Orchestrator.Services;

public interface ICommandLifecycleTracker
{
    CommandDispatchRegistration RegisterDispatch(string commandId, string droneId, SemaphoreSlim pacingToken, DomainLease? domainLease);

    Task<bool> WaitForAcknowledgementAsync(string commandId, TimeSpan timeout, CancellationToken cancellationToken);

    void MarkAcknowledged(string commandId, string droneId);

    void Complete(string commandId, string droneId);

    void Fail(string commandId, string droneId, string reason);

    void FailAll(string droneId, string reason);
}

public sealed record CommandDispatchRegistration(string CommandId, string DroneId);
