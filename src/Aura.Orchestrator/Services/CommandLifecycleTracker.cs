using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Aura.Orchestrator.Security;

namespace Aura.Orchestrator.Services;

public sealed class CommandLifecycleTracker : ICommandLifecycleTracker
{
    private readonly ILogger<CommandLifecycleTracker> _logger;
    private readonly ConcurrentDictionary<string, CommandState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CommandAcknowledgementResult> _completionResults = new(StringComparer.OrdinalIgnoreCase);

    public CommandLifecycleTracker(ILogger<CommandLifecycleTracker> logger)
    {
        _logger = logger;
    }

    public CommandDispatchRegistration RegisterDispatch(string commandId, string droneId, SemaphoreSlim pacingToken, DomainLease? domainLease)
    {
        var state = new CommandState(droneId, pacingToken, domainLease);
        _completionResults.TryRemove(commandId, out _);
        if (!_states.TryAdd(commandId, state))
        {
            throw new InvalidOperationException($"Command {commandId} is already tracked.");
        }

        return new CommandDispatchRegistration(commandId, droneId);
    }

    public async Task<CommandAcknowledgementResult> WaitForAcknowledgementAsync(string commandId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_states.TryGetValue(commandId, out var state))
        {
            if (_completionResults.TryRemove(commandId, out var completed))
            {
                return completed;
            }

            return CommandAcknowledgementResult.Acknowledged;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, linkedCts.Token);
        var ackTask = state.AcknowledgedTask;
        var completed = await Task.WhenAny(ackTask, delayTask).ConfigureAwait(false);
        if (completed == ackTask)
        {
            linkedCts.Cancel();
            return await ackTask.ConfigureAwait(false);
        }

        return CommandAcknowledgementResult.Timeout;
    }

    public void MarkAcknowledged(string commandId, string droneId)
    {
        if (!_states.TryGetValue(commandId, out var state))
        {
            _logger.LogDebug("Acknowledgment for unknown command {CommandId}", commandId);
            return;
        }

        if (!state.BelongsTo(droneId))
        {
            _logger.LogWarning("Command {CommandId} acknowledged by unexpected drone {DroneId}", commandId, droneId);
        }

        state.MarkAcknowledged();
    }

    public void Complete(string commandId, string droneId)
    {
        if (_states.TryRemove(commandId, out var state))
        {
            state.MarkAcknowledged();
            state.ReleaseResources();
            _completionResults[commandId] = CommandAcknowledgementResult.Acknowledged;
        }
        else
        {
            _logger.LogDebug("Completion received for unknown command {CommandId}", commandId);
        }
    }

    public void Fail(string commandId, string droneId, string reason)
    {
        if (_states.TryRemove(commandId, out var state))
        {
            state.MarkFailed(reason);
            state.ReleaseResources();
            _completionResults[commandId] = new CommandAcknowledgementResult(CommandAcknowledgementStatus.Failed, reason);
        }
        else
        {
            _logger.LogDebug("Failure received for unknown command {CommandId}", commandId);
        }
    }

    public void FailAll(string droneId, string reason)
    {
        foreach (var entry in _states.ToArray())
        {
            if (entry.Value.BelongsTo(droneId) && _states.TryRemove(entry.Key, out var state))
            {
                state.MarkFailed(reason);
                state.ReleaseResources();
                _completionResults[entry.Key] = new CommandAcknowledgementResult(CommandAcknowledgementStatus.Failed, reason);
            }
        }
    }

    private sealed class CommandState
    {
        private readonly TaskCompletionSource<CommandAcknowledgementResult> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CommandState(string droneId, SemaphoreSlim pacingToken, DomainLease? domainLease)
        {
            DroneId = droneId;
            PacingToken = pacingToken;
            DomainLease = domainLease;
        }

        public string DroneId { get; }
        public SemaphoreSlim PacingToken { get; }
        public DomainLease? DomainLease { get; }
        public Task<CommandAcknowledgementResult> AcknowledgedTask => _acknowledged.Task;

        public bool BelongsTo(string droneId) => string.Equals(DroneId, droneId, StringComparison.OrdinalIgnoreCase);

        public void MarkAcknowledged()
        {
            _acknowledged.TrySetResult(CommandAcknowledgementResult.Acknowledged);
        }

        public void MarkFailed(string reason)
        {
            _acknowledged.TrySetResult(new CommandAcknowledgementResult(CommandAcknowledgementStatus.Failed, reason));
        }

        public void ReleaseResources()
        {
            DomainLease?.Dispose();
            PacingToken.Release();
        }
    }
}
