using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aura.Orchestrator.Scheduling;

public sealed record DeadLetterCommand(
    string CommandId,
    string Reason,
    string? PersonaId,
    string? DroneId,
    int RetryCount,
    DateTime FailedAtUtc,
    IReadOnlyDictionary<string, object?>? Metadata);

public interface IDeadLetterQueue
{
    Task PublishAsync(DeadLetterCommand command, CancellationToken cancellationToken = default);
}

public sealed class NullDeadLetterQueue : IDeadLetterQueue
{
    public static NullDeadLetterQueue Instance { get; } = new();

    private NullDeadLetterQueue()
    {
    }

    public Task PublishAsync(DeadLetterCommand command, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
