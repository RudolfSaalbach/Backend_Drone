namespace Aura.Orchestrator.Configuration;

/// <summary>
/// Strongly typed configuration model for the orchestrator components.
/// </summary>
public sealed class OrchestratorConfig
{
    public SchedulingConfig Scheduling { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public ServerConfig Server { get; set; } = new();
}

public sealed class SchedulingConfig
{
    public ReadyQueueConfig ReadyQueue { get; set; } = new();
    public PerDroneQueueConfig PerDroneQueue { get; set; } = new();

    /// <summary>
    /// Maximum allowed in-flight commands per drone.
    /// </summary>
    public int MaxInFlightPerDrone { get; set; } = 1;

    /// <summary>
    /// Seconds the scheduler waits for an acknowledgement before timing out.
    /// </summary>
    public int AckTimeoutSec { get; set; } = 20;

    /// <summary>
    /// Seconds after which a missing heartbeat triggers a warning.
    /// </summary>
    public int HeartbeatExpectSec { get; set; } = 30;

    /// <summary>
    /// Grace period in seconds before a disconnected drone is deregistered.
    /// </summary>
    public int DisconnectGraceSec { get; set; } = 60;

    /// <summary>
    /// Sleep interval between per drone queue scans.
    /// </summary>
    public int DispatchLoopDelayMs { get; set; } = 100;

    /// <summary>
    /// Maximum number of times a task will be retried when its persona cannot be loaded.
    /// </summary>
    public int PersonaMissingMaxRetries { get; set; } = 5;

    /// <summary>
    /// Base delay in seconds before retrying a task that failed to load a persona.
    /// Exponential backoff is applied on top of this base value.
    /// </summary>
    public int PersonaMissingBaseDelaySec { get; set; } = 5;

    /// <summary>
    /// Upper bound in seconds for the persona retry backoff delay.
    /// </summary>
    public int PersonaMissingMaxBackoffSec { get; set; } = 120;
}

public sealed class ReadyQueueConfig
{
    public int Capacity { get; set; } = 1000;
}

public sealed class PerDroneQueueConfig
{
    public int Capacity { get; set; } = 10;
}

public sealed class LimitsConfig
{
    public GlobalDomainLimits Global { get; set; } = new();
    public PerDomainLimits PerDomain { get; set; } = new();

    /// <summary>
    /// Seconds before idle domain limiter state is trimmed from memory.
    /// </summary>
    public int DomainStateTtlSeconds { get; set; } = 600;
}

public sealed class GlobalDomainLimits
{
    /// <summary>
    /// Maximum concurrent sessions per domain across all drones.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 25;
}

public sealed class PerDomainLimits
{
    /// <summary>
    /// Maximum parallel sessions a single drone may run for a domain.
    /// </summary>
    public int ConcurrencyPerDrone { get; set; } = 1;

    /// <summary>
    /// Allowed requests per second for a single drone on a domain.
    /// </summary>
    public double QpsPerDrone { get; set; } = 2.0;

    /// <summary>
    /// Number of consecutive requests that trigger a cooldown.
    /// </summary>
    public int BurstLimit { get; set; } = 3;

    /// <summary>
    /// Cooldown duration in seconds once the burst limit has been exceeded.
    /// </summary>
    public int CooldownSeconds { get; set; } = 30;
}

public sealed class ServerConfig
{
    public string ApiKey { get; set; } = string.Empty;
}
