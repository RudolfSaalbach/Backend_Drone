using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Aura.Orchestrator.Communication;
using Aura.Orchestrator.Configuration;
using Aura.Orchestrator.Metrics;
using Aura.Orchestrator.Models;
using Aura.Orchestrator.Security;
using Aura.Orchestrator.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Aura.Orchestrator.Scheduling;

public sealed class OrchestratorTaskScheduler : BackgroundService
{
    private readonly ILogger<OrchestratorTaskScheduler> _logger;
    private readonly OrchestratorConfig _config;
    private readonly IDroneRegistry _droneRegistry;
    private readonly IPersonaLibrary _personaLibrary;
    private readonly IDomainLimiter _domainLimiter;
    private readonly ICommandLifecycleTracker _commandLifecycle;
    private readonly IHubContext<DroneHub> _hubContext;
    private readonly IMetricsCollector _metrics;

    private readonly Channel<ScheduledTask> _globalReadyQueue;
    private readonly ConcurrentDictionary<string, Channel<ScheduledTask>> _perDroneQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pacingTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _perDroneQueueLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAssignment = new(StringComparer.OrdinalIgnoreCase);
    private int _globalQueueLength;

    public OrchestratorTaskScheduler(
        ILogger<OrchestratorTaskScheduler> logger,
        IOptions<OrchestratorConfig> config,
        IDroneRegistry droneRegistry,
        IPersonaLibrary personaLibrary,
        IDomainLimiter domainLimiter,
        ICommandLifecycleTracker commandLifecycle,
        IHubContext<DroneHub> hubContext,
        IMetricsCollector metrics)
    {
        _logger = logger;
        _config = config.Value;
        _droneRegistry = droneRegistry;
        _personaLibrary = personaLibrary;
        _domainLimiter = domainLimiter;
        _commandLifecycle = commandLifecycle;
        _hubContext = hubContext;
        _metrics = metrics;

        var options = new BoundedChannelOptions(_config.Scheduling.ReadyQueue.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _globalReadyQueue = Channel.CreateBounded<ScheduledTask>(options);
    }

    public async Task<bool> EnqueueTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        if (!ValidateTask(task, out var validationError))
        {
            _logger.LogWarning("Rejected task {CommandId}: {Reason}", task.CommandId, validationError);
            return false;
        }

        await _globalReadyQueue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
        var length = Interlocked.Increment(ref _globalQueueLength);
        _metrics.RecordGauge("queue_global_length", length);
        _metrics.IncrementCounter("tasks_enqueued_total");
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting orchestrator task scheduler");
        var processors = new[]
        {
            ProcessGlobalQueueAsync(stoppingToken),
            ProcessPerDroneQueuesAsync(stoppingToken),
            MonitorQueueMetricsAsync(stoppingToken)
        };

        return Task.WhenAll(processors);
    }

    private async Task ProcessGlobalQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var task in _globalReadyQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _globalQueueLength);
            try
            {
                var eligible = await FindEligibleDronesAsync(task, cancellationToken).ConfigureAwait(false);
                if (eligible.Count == 0)
                {
                    _logger.LogWarning("No eligible drone available for task {CommandId}", task.CommandId);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var selected = SelectDrone(eligible, task);
                if (selected == null)
                {
                    _logger.LogWarning("Unable to select drone for task {CommandId}", task.CommandId);
                    await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await EnqueuePerDroneAsync(selected.DroneId, task, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process task {CommandId} from global queue", task.CommandId);
            }
        }
    }

    private async Task ProcessPerDroneQueuesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var (droneId, queue) in _perDroneQueues)
            {
                if (!await IsDroneAvailableAsync(droneId, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (queue.Reader.TryRead(out var task))
                {
                    AdjustPerDroneQueueLength(droneId, -1);
                    await DispatchAsync(droneId, task, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(_config.Scheduling.DispatchLoopDelayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorQueueMetricsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordGauge("queue_global_length", Volatile.Read(ref _globalQueueLength));
            foreach (var (droneId, length) in _perDroneQueueLengths)
            {
                _metrics.RecordGauge("queue_per_drone_length", length, new Dictionary<string, string> { ["drone_id"] = droneId });
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<DroneInfo>> FindEligibleDronesAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var active = await _droneRegistry.GetActiveDronesAsync(cancellationToken).ConfigureAwait(false);
        var eligible = new List<DroneInfo>();
        foreach (var drone in active)
        {
            if (_lastAssignment.TryGetValue(drone.DroneId, out var lastAssigned))
            {
                drone.LastTaskAssignedAt = lastAssigned;
            }

            if (MatchesCapabilities(drone, task.RequiredCapabilities))
            {
                eligible.Add(drone);
            }
        }

        return eligible;
    }

    private static bool MatchesCapabilities(DroneInfo drone, IReadOnlyCollection<string> required)
    {
        if (required == null || required.Count == 0)
        {
            return true;
        }

        if (drone.StaticCapabilities == null)
        {
            return false;
        }

        return required.All(cap => drone.StaticCapabilities.Contains(cap));
    }

    private DroneInfo? SelectDrone(IReadOnlyCollection<DroneInfo> drones, ScheduledTask task)
    {
        return drones
            .OrderBy(d => d.CurrentLoad)
            .ThenBy(d => d.LastTaskAssignedAt)
            .ThenByDescending(d => CalculateScore(d, task))
            .FirstOrDefault();
    }

    private static double CalculateScore(DroneInfo drone, ScheduledTask task)
    {
        var score = 1.0;
        if (drone.StaticCapabilities != null && task.RequiredCapabilities != null)
        {
            var matches = drone.StaticCapabilities.Intersect(task.RequiredCapabilities).Count();
            score += matches * 0.1;
        }

        var idleMinutes = (DateTime.UtcNow - drone.LastTaskAssignedAt).TotalMinutes;
        score += Math.Min(idleMinutes * 0.01, 0.5);
        score -= drone.CurrentLoad * 0.2;
        return score;
    }

    private async Task EnqueuePerDroneAsync(string droneId, ScheduledTask task, CancellationToken cancellationToken)
    {
        var queue = _perDroneQueues.GetOrAdd(droneId, _ => CreatePerDroneQueue());
        await queue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
        AdjustPerDroneQueueLength(droneId, +1);
        _metrics.IncrementCounter("tasks_queued_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
    }

    private Channel<ScheduledTask> CreatePerDroneQueue()
    {
        var options = new BoundedChannelOptions(_config.Scheduling.PerDroneQueue.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        return Channel.CreateBounded<ScheduledTask>(options);
    }

    private async Task DispatchAsync(string droneId, ScheduledTask task, CancellationToken cancellationToken)
    {
        var pacingToken = _pacingTokens.GetOrAdd(droneId, _ => new SemaphoreSlim(_config.Scheduling.MaxInFlightPerDrone, _config.Scheduling.MaxInFlightPerDrone));
        if (!await pacingToken.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await EnqueuePerDroneAsync(droneId, task, cancellationToken).ConfigureAwait(false);
            return;
        }

        DomainLease? domainLease = null;
        try
        {
            if (!string.IsNullOrEmpty(task.Domain))
            {
                domainLease = await _domainLimiter.TryAcquireAsync(droneId, task.Domain, cancellationToken).ConfigureAwait(false);
                if (domainLease == null)
                {
                    _logger.LogDebug("Domain limiter blocked task {CommandId} for {Domain}", task.CommandId, task.Domain);
                    pacingToken.Release();
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    await EnqueuePerDroneAsync(droneId, task, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            var persona = await _personaLibrary.LoadPersonaAsync(task.PersonaId, cancellationToken).ConfigureAwait(false);
            if (persona == null)
            {
                _logger.LogError("Persona {PersonaId} not found for task {CommandId}", task.PersonaId, task.CommandId);
                pacingToken.Release();
                domainLease?.Dispose();
                return;
            }

            var commandPayload = new CommandPayload
            {
                CommandId = task.CommandId,
                Type = task.Type,
                Parameters = task.Parameters ?? new JObject(),
                Persona = persona,
                Session = task.Session,
                TimeoutSec = task.TimeoutSec
            };

            await _hubContext.SendCommandToDroneAsync(droneId, commandPayload, cancellationToken).ConfigureAwait(false);

            await _droneRegistry.UpdateDroneStatusAsync(droneId, new StatusPayload
            {
                Status = "busy",
                CurrentCommand = task.CommandId
            }, cancellationToken).ConfigureAwait(false);

            _lastAssignment[droneId] = DateTime.UtcNow;

            _commandLifecycle.RegisterDispatch(task.CommandId, droneId, pacingToken, domainLease);
            domainLease = null;
            _metrics.IncrementCounter("tasks_dispatched_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
            _ = MonitorAckTimeoutAsync(task, droneId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch task {CommandId} to drone {DroneId}", task.CommandId, droneId);
            pacingToken.Release();
            domainLease?.Dispose();
            await EnqueuePerDroneAsync(droneId, task, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorAckTimeoutAsync(ScheduledTask task, string droneId, CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(_config.Scheduling.AckTimeoutSec);
            var ackResult = await _commandLifecycle.WaitForAcknowledgementAsync(task.CommandId, timeout, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (ackResult.Status == CommandAcknowledgementStatus.Acknowledged)
            {
                return;
            }

            if (ackResult.Status == CommandAcknowledgementStatus.Failed)
            {
                _logger.LogDebug(
                    "Command {CommandId} concluded with failure reason {Reason} before acknowledgement",
                    task.CommandId,
                    ackResult.FailureReason ?? "unknown");

                if (string.Equals(ackResult.FailureReason, "drone_disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Requeueing command {CommandId} after drone {DroneId} disconnected before acknowledgement",
                        task.CommandId,
                        droneId);
                    task.EnqueuedAt = DateTime.UtcNow;
                    await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);
                    _metrics.IncrementCounter("tasks_requeued_total");
                }

                return;
            }

            _logger.LogWarning("Ack timeout for command {CommandId} on drone {DroneId}", task.CommandId, droneId);
            _metrics.IncrementCounter("commands_ack_timeout_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
            _commandLifecycle.Fail(task.CommandId, droneId, "ack_timeout");
            await _droneRegistry.IncrementDroneErrorCountAsync(droneId, cancellationToken).ConfigureAwait(false);
            await _droneRegistry.UpdateDroneStatusAsync(droneId, new StatusPayload
            {
                Status = "idle",
                CurrentCommand = string.Empty
            }, cancellationToken).ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                task.EnqueuedAt = DateTime.UtcNow;
                await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);
                _metrics.IncrementCounter("tasks_requeued_total");
            }
        }
        catch (OperationCanceledException)
        {
            // Scheduler is shutting down; no requeue required.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle ack timeout for command {CommandId}", task.CommandId);
        }
    }

    private async Task<bool> IsDroneAvailableAsync(string droneId, CancellationToken cancellationToken)
    {
        var drone = await _droneRegistry.GetDroneAsync(droneId, cancellationToken).ConfigureAwait(false);
        if (drone == null)
        {
            _perDroneQueues.TryRemove(droneId, out _);
            _pacingTokens.TryRemove(droneId, out _);
            _perDroneQueueLengths.TryRemove(droneId, out _);
            _lastAssignment.TryRemove(droneId, out _);
            return false;
        }

        if (drone.Status != DroneStatus.Idle)
        {
            return false;
        }

        if (_pacingTokens.TryGetValue(droneId, out var token) && token.CurrentCount == 0)
        {
            return false;
        }

        return true;
    }

    private bool ValidateTask(ScheduledTask task, out string reason)
    {
        if (string.IsNullOrWhiteSpace(task.CommandId))
        {
            reason = "missing_command_id";
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.PersonaId))
        {
            reason = "missing_persona";
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.Type))
        {
            reason = "missing_type";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void AdjustPerDroneQueueLength(string droneId, int delta)
    {
        _perDroneQueueLengths.AddOrUpdate(droneId, delta, (_, current) => Math.Max(0, current + delta));
    }
}
