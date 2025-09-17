using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
    private readonly ConcurrentDictionary<string, Task> _perDroneProcessors = new(StringComparer.OrdinalIgnoreCase);
    private int _globalQueueLength;
    private CancellationToken _schedulerStoppingToken;
    private readonly CancellationTokenSource _processorCts = new();

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
        _schedulerStoppingToken = stoppingToken;
        stoppingToken.Register(() =>
        {
            _processorCts.Cancel();
            foreach (var queue in _perDroneQueues.Values)
            {
                queue.Writer.TryComplete();
            }
        });
        _logger.LogInformation("Starting orchestrator task scheduler");
        var processors = new[]
        {
            ProcessGlobalQueueAsync(stoppingToken),
            MonitorPerDroneProcessorsAsync(stoppingToken),
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

    private async Task MonitorPerDroneProcessorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var (droneId, processor) in _perDroneProcessors)
                {
                    if (processor.IsFaulted)
                    {
                        _logger.LogError(processor.Exception, "Per-drone processor faulted for {DroneId}", droneId);
                        _perDroneProcessors.TryRemove(droneId, out _);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Scheduler is stopping, fall through to await outstanding processors.
        }
        finally
        {
            if (_perDroneProcessors.Count > 0)
            {
                try
                {
                    await Task.WhenAll(_perDroneProcessors.Values).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Per-drone processors completed with errors during shutdown");
                }
            }
        }
    }

    private async Task ProcessPerDroneQueueAsync(string droneId, Channel<ScheduledTask> queue, CancellationToken cancellationToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await IsDroneAvailableAsync(droneId, cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(_config.Scheduling.DispatchLoopDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!queue.Reader.TryRead(out var task))
                {
                    continue;
                }

                AdjustPerDroneQueueLength(droneId, -1);
                await DispatchAsync(droneId, task, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Scheduler is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process queue for drone {DroneId}", droneId);
        }
        finally
        {
            _perDroneProcessors.TryRemove(droneId, out _);
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
        var queue = _perDroneQueues.GetOrAdd(droneId, id => CreatePerDroneQueue(id));
        await queue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
        AdjustPerDroneQueueLength(droneId, +1);
        _metrics.IncrementCounter("tasks_queued_total", 1, new Dictionary<string, string> { ["drone_id"] = droneId });
    }

    private Channel<ScheduledTask> CreatePerDroneQueue(string droneId)
    {
        var options = new BoundedChannelOptions(_config.Scheduling.PerDroneQueue.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        var channel = Channel.CreateBounded<ScheduledTask>(options);
        StartPerDroneProcessor(droneId, channel);
        return channel;
    }

    private void StartPerDroneProcessor(string droneId, Channel<ScheduledTask> queue)
    {
        var processor = Task.Run(() => ProcessPerDroneQueueAsync(droneId, queue, _processorCts.Token), CancellationToken.None);
        if (!_perDroneProcessors.TryAdd(droneId, processor))
        {
            _logger.LogWarning("Attempted to register multiple processors for drone {DroneId}", droneId);
        }
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
                pacingToken.Release();
                domainLease?.Dispose();
                await HandleMissingPersonaAsync(task, cancellationToken).ConfigureAwait(false);
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
            if (_perDroneQueues.TryRemove(droneId, out var removedQueue))
            {
                removedQueue.Writer.TryComplete();
            }
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

    private Task HandleMissingPersonaAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        task.PersonaRetryCount++;
        var attempts = task.PersonaRetryCount;
        var personaIdLabel = string.IsNullOrWhiteSpace(task.PersonaId) ? "unknown" : task.PersonaId;

        if (attempts > _config.Scheduling.PersonaMissingMaxRetries)
        {
            _logger.LogError(
                "Persona {PersonaId} not found for command {CommandId} after {Attempts} attempts; giving up",
                personaIdLabel,
                task.CommandId,
                attempts);
            _metrics.IncrementCounter(
                "tasks_persona_missing_failed_total",
                1,
                new Dictionary<string, string> { ["persona_id"] = personaIdLabel });
            _commandLifecycle.Fail(task.CommandId, string.Empty, "missing_persona");
            return Task.CompletedTask;
        }

        var delay = CalculatePersonaRetryDelay(attempts);
        _logger.LogWarning(
            "Persona {PersonaId} unavailable for command {CommandId}; retrying in {DelaySeconds:F1}s (attempt {Attempt})",
            personaIdLabel,
            task.CommandId,
            delay.TotalSeconds,
            attempts);

        _metrics.IncrementCounter(
            "tasks_persona_missing_retry_total",
            1,
            new Dictionary<string, string>
            {
                ["persona_id"] = personaIdLabel,
                ["attempt"] = attempts.ToString(CultureInfo.InvariantCulture)
            });

        _ = ScheduleTaskRequeueAsync(task, delay, cancellationToken, personaIdLabel);
        return Task.CompletedTask;
    }

    private TimeSpan CalculatePersonaRetryDelay(int attempt)
    {
        var baseDelay = Math.Max(1, _config.Scheduling.PersonaMissingBaseDelaySec);
        var maxDelay = Math.Max(baseDelay, _config.Scheduling.PersonaMissingMaxBackoffSec);
        var exponential = baseDelay * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, maxDelay);
        var jitterMultiplier = 0.75 + (Random.Shared.NextDouble() * 0.5);
        var jittered = Math.Max(baseDelay, capped * jitterMultiplier);
        return TimeSpan.FromSeconds(jittered);
    }

    private Task ScheduleTaskRequeueAsync(ScheduledTask task, TimeSpan delay, CancellationToken cancellationToken, string personaIdLabel)
    {
        return Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                task.EnqueuedAt = DateTime.UtcNow;
                await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);

                _metrics.IncrementCounter("tasks_requeued_total");
                _metrics.IncrementCounter(
                    "tasks_persona_missing_requeued_total",
                    1,
                    new Dictionary<string, string>
                    {
                        ["persona_id"] = personaIdLabel,
                        ["attempt"] = task.PersonaRetryCount.ToString(CultureInfo.InvariantCulture)
                    });
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Skipping persona retry requeue for command {CommandId} due to shutdown", task.CommandId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to requeue command {CommandId} after persona backoff", task.CommandId);
            }
        }, CancellationToken.None);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _processorCts.Cancel();
            _processorCts.Dispose();
        }

        base.Dispose(disposing);
    }
}
