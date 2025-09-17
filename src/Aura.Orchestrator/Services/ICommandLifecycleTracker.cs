using System.Threading;
using Aura.Orchestrator.Security;

namespace Aura.Orchestrator.Services;

public interface ICommandLifecycleTracker
{
    CommandDispatchRegistration RegisterDispatch(string commandId, string droneId, SemaphoreSlim pacingToken, DomainLease? domainLease);

    Task<CommandAcknowledgementResult> WaitForAcknowledgementAsync(string commandId, TimeSpan timeout, CancellationToken cancellationToken);

    void MarkAcknowledged(string commandId, string droneId);

    void Complete(string commandId, string droneId);

    void Fail(string commandId, string droneId, string reason);

    void FailAll(string droneId, string reason);
}

public sealed record CommandDispatchRegistration(string CommandId, string DroneId);

public enum CommandAcknowledgementStatus
{
    Acknowledged,
    Failed,
    Timeout
}

public sealed record CommandAcknowledgementResult(CommandAcknowledgementStatus Status, string? FailureReason = null)
{
    public static CommandAcknowledgementResult Acknowledged { get; } = new(CommandAcknowledgementStatus.Acknowledged);
    public static CommandAcknowledgementResult Timeout { get; } = new(CommandAcknowledgementStatus.Timeout);
}
