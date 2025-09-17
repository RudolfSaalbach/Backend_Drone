using Newtonsoft.Json;

namespace Aura.Orchestrator.Models;

public enum DroneStatus
{
    Idle,
    Busy,
    Disconnected,
    Error
}

public sealed class DroneInfo
{
    public string DroneId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public IReadOnlyCollection<string> StaticCapabilities { get; set; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Modules { get; set; } = Array.Empty<string>();
    public LimitsSupport LimitsSupported { get; set; } = new();
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public DateTime LastTaskAssignedAt { get; set; } = DateTime.MinValue;
    public DroneStatus Status { get; set; } = DroneStatus.Idle;
    public double CurrentLoad { get; set; }
    public int ErrorCount { get; set; }
}

public sealed class LimitsSupport
{
    [JsonProperty("domain_guard")]
    public bool DomainGuard { get; set; }

    [JsonProperty("qps_gate")]
    public bool QpsGate { get; set; }
}
