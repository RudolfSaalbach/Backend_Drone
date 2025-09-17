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
using Aura.Orchestrator.Interventions;
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

    private readonly PriorityTaskQueue _globalReadyQueue;
    private readonly ConcurrentDictionary<string, Channel<ScheduledTask>> _perDroneQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pacingTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _perDroneQueueLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAssignment = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _perDroneProcessors = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<IInterventionNotificationSink> _interventionSinks;
    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly object _personaRetryLock = new();
    private readonly PriorityQueue<PersonaRetryWorkItem, DateTime> _personaRetryQueue = new();
    private readonly AsyncAutoResetEvent _personaRetrySignal = new();
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
        IMetricsCollector metrics,
        IEnumerable<IInterventionNotificationSink>? interventionSinks = null,
        IDeadLetterQueue? deadLetterQueue = null)
    {
        _logger = logger;
        _config = config.Value;
        _droneRegistry = droneRegistry;
        _personaLibrary = personaLibrary;
        _domainLimiter = domainLimiter;
        _commandLifecycle = commandLifecycle;
        _hubContext = hubContext;
        _metrics = metrics;
        _interventionSinks = interventionSinks?.ToArray() ?? Array.Empty<IInterventionNotificationSink>();
        _deadLetterQueue = deadLetterQueue ?? NullDeadLetterQueue.Instance;

        _globalReadyQueue = new PriorityTaskQueue(_config.Scheduling.ReadyQueue.Capacity);
    }

    public async Task<bool> EnqueueTaskAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        if (!ValidateTask(task, out var validationError))
        {
            _logger.LogWarning("Rejected task {CommandId}: {Reason}", task.CommandId, validationError);
            return false;
        }

        task.EnqueuedAt = DateTime.UtcNow;
        await _globalReadyQueue.EnqueueAsync(task, cancellationToken).ConfigureAwait(false);
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
            _globalReadyQueue.Complete();
            _personaRetrySignal.Set();
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
            MonitorQueueMetricsAsync(stoppingToken),
            ProcessPersonaRetryQueueAsync(stoppingToken)
        };

        return Task.WhenAll(processors);
    }

    private async Task ProcessGlobalQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ScheduledTask? task = null;
            try
            {
                task = await _globalReadyQueue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (task == null)
            {
                break;
            }

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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
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
                        if (_perDroneQueues.TryGetValue(droneId, out var queue) &&
                            !queue.Reader.Completion.IsCompleted &&
                            !_processorCts.IsCancellationRequested)
                        {
                            _logger.LogInformation("Restarting per-drone processor for {DroneId}", droneId);
                            StartPerDroneProcessor(droneId, queue);
                        }
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
                while (queue.Reader.TryRead(out var task))
                {
                    AdjustPerDroneQueueLength(droneId, -1);
                    await DispatchAsync(droneId, task, cancellationToken).ConfigureAwait(false);
                }
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
        score += (int)task.Priority * 0.3;
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
            var drone = await _droneRegistry.GetDroneAsync(droneId, cancellationToken).ConfigureAwait(false);
            if (drone == null)
            {
                _logger.LogWarning(
                    "Drone {DroneId} was not found while dispatching command {CommandId}; returning to global queue",
                    droneId,
                    task.CommandId);
                if (_perDroneQueues.TryRemove(droneId, out var removedQueue))
                {
                    removedQueue.Writer.TryComplete();
                }
                _pacingTokens.TryRemove(droneId, out _);
                _perDroneQueueLengths.TryRemove(droneId, out _);
                _lastAssignment.TryRemove(droneId, out _);
                pacingToken.Release();
                await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (drone.Status != DroneStatus.Idle)
            {
                _logger.LogDebug(
                    "Drone {DroneId} is not idle (current status: {Status}); returning command {CommandId} to global queue",
                    droneId,
                    drone.Status,
                    task.CommandId);
                pacingToken.Release();
                await EnqueueTaskAsync(task, cancellationToken).ConfigureAwait(false);
                return;
            }

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

    private async Task HandleMissingPersonaAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        task.PersonaRetryCount++;
        var attempts = task.PersonaRetryCount;
        var personaIdLabel = string.IsNullOrWhiteSpace(task.PersonaId) ? "unknown" : task.PersonaId;

        if (attempts > _config.Scheduling.PersonaMissingMaxRetries)
        {
            _logger.LogError(
                "Persona {PersonaId} not found for command {CommandId} after {Attempts} attempts; routing to dead letter",
                personaIdLabel,
                task.CommandId,
                attempts);
            _metrics.IncrementCounter(
                "tasks_persona_missing_failed_total",
                1,
                new Dictionary<string, string> { ["persona_id"] = personaIdLabel });

            await NotifyPersonaFailureAsync(task, personaIdLabel, cancellationToken).ConfigureAwait(false);
            _commandLifecycle.Fail(task.CommandId, string.Empty, "missing_persona");
            return;
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

        SchedulePersonaRetry(task, delay, personaIdLabel, attempts);
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

    private void SchedulePersonaRetry(ScheduledTask task, TimeSpan delay, string personaIdLabel, int attempt)
    {
        var dueAt = DateTime.UtcNow.Add(delay);
        lock (_personaRetryLock)
        {
            _personaRetryQueue.Enqueue(new PersonaRetryWorkItem(task, dueAt, personaIdLabel, attempt), dueAt);
        }

        _personaRetrySignal.Set();
    }

    private async Task NotifyPersonaFailureAsync(ScheduledTask task, string personaIdLabel, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["persona_id"] = personaIdLabel,
            ["attempts"] = task.PersonaRetryCount,
            ["requested_at"] = task.EnqueuedAt,
            ["node_id"] = string.IsNullOrWhiteSpace(task.NodeId) ? null : task.NodeId,
            ["process_id"] = string.IsNullOrWhiteSpace(task.ProcessId) ? null : task.ProcessId,
            ["command_type"] = task.Type
        };

        await _deadLetterQueue.PublishAsync(
            new DeadLetterCommand(
                task.CommandId,
                "missing_persona",
                personaIdLabel,
                string.Empty,
                task.PersonaRetryCount,
                DateTime.UtcNow,
                metadata),
            cancellationToken).ConfigureAwait(false);

        if (_interventionSinks.Count == 0)
        {
            return;
        }

        var notificationMetadata = new Dictionary<string, object?>(metadata)
        {
            ["reason"] = "persona_unavailable"
        };

        var notification = new InterventionNotification(
            task.CommandId,
            string.Empty,
            "persona_missing",
            "persona_unavailable",
            DateTime.UtcNow,
            notificationMetadata);

        foreach (var sink in _interventionSinks)
        {
            try
            {
                await sink.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Persona failure notification via {Sink} failed for command {CommandId}",
                    sink.GetType().Name,
                    task.CommandId);
            }
        }
    }

    private async Task ProcessPersonaRetryQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PersonaRetryWorkItem? workItem = null;
                TimeSpan waitTime = Timeout.InfiniteTimeSpan;

                lock (_personaRetryLock)
                {
                    if (_personaRetryQueue.Count > 0)
                    {
                        var next = _personaRetryQueue.Peek();
                        var now = DateTime.UtcNow;
                        if (next.DueAtUtc <= now)
                        {
                            workItem = _personaRetryQueue.Dequeue();
                        }
                        else
                        {
                            waitTime = next.DueAtUtc - now;
                        }
                    }
                }

                if (workItem.HasValue)
                {
                    var item = workItem.Value;
                    try
                    {
                        await EnqueueTaskAsync(item.Task, cancellationToken).ConfigureAwait(false);
                        _metrics.IncrementCounter("tasks_requeued_total");
                        _metrics.IncrementCounter(
                            "tasks_persona_missing_requeued_total",
                            1,
                            new Dictionary<string, string>
                            {
                                ["persona_id"] = item.PersonaIdLabel,
                                ["attempt"] = item.Attempt.ToString(CultureInfo.InvariantCulture)
                            });
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to requeue command {CommandId} after persona backoff", item.Task.CommandId);
                    }

                    continue;
                }

                if (waitTime == Timeout.InfiniteTimeSpan)
                {
                    await _personaRetrySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var delayTask = Task.Delay(waitTime, cancellationToken);
                    var signalTask = _personaRetrySignal.WaitAsync(cancellationToken);
                    var completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
                    if (completed == signalTask)
                    {
                        await signalTask.ConfigureAwait(false);
                    }
                    else
                    {
                        await delayTask.ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private readonly record struct PersonaRetryWorkItem(ScheduledTask Task, DateTime DueAtUtc, string PersonaIdLabel, int Attempt);

    private sealed class AsyncAutoResetEvent
    {
        private readonly SemaphoreSlim _semaphore = new(0, int.MaxValue);

        public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

        public void Set()
        {
            _semaphore.Release();
        }
    }

    private sealed class PriorityTaskQueue : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _items = new(0, int.MaxValue);
        private readonly object _sync = new();
        private readonly PriorityQueue<ScheduledTask, QueueKey> _queue = new();
        private long _sequence;
        private int _count;
        private bool _completed;

        public PriorityTaskQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _slots = new SemaphoreSlim(capacity, capacity);
        }

        public int Count => Volatile.Read(ref _count);

        public async Task EnqueueAsync(ScheduledTask task, CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_completed)
                {
                    _slots.Release();
                    throw new InvalidOperationException("Priority queue has been completed.");
                }

                var key = new QueueKey(-(int)task.Priority, task.EnqueuedAt, Interlocked.Increment(ref _sequence));
                _queue.Enqueue(task, key);
                Interlocked.Increment(ref _count);
            }

            _items.Release();
        }

        public async Task<ScheduledTask?> DequeueAsync(CancellationToken cancellationToken)
        {
            await _items.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    _slots.Release();
                    return null;
                }

                var task = _queue.Dequeue();
                Interlocked.Decrement(ref _count);
                _slots.Release();
                return task;
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                _completed = true;
            }

            _items.Release();
        }

        public void Dispose()
        {
            _slots.Dispose();
            _items.Dispose();
        }

        private readonly record struct QueueKey(int PriorityScore, DateTime EnqueuedAtUtc, long Sequence) : IComparable<QueueKey>
        {
            public int CompareTo(QueueKey other)
            {
                var priorityCompare = PriorityScore.CompareTo(other.PriorityScore);
                if (priorityCompare != 0)
                {
                    return priorityCompare;
                }

                var timeCompare = EnqueuedAtUtc.CompareTo(other.EnqueuedAtUtc);
                if (timeCompare != 0)
                {
                    return timeCompare;
                }

                return Sequence.CompareTo(other.Sequence);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _processorCts.Cancel();
            _processorCts.Dispose();
            _globalReadyQueue.Dispose();
        }

        base.Dispose(disposing);
    }
}
