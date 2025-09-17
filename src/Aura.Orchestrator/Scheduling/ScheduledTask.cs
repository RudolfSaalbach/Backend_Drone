using Newtonsoft.Json.Linq;
using Aura.Orchestrator.Models;

namespace Aura.Orchestrator.Scheduling;

public sealed class ScheduledTask
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public string PersonaId { get; set; } = string.Empty;
    public IReadOnlyCollection<string> RequiredCapabilities { get; set; } = Array.Empty<string>();
    public string? Domain { get; set; }
    public JObject Parameters { get; set; } = new();
    public SessionInfo Session { get; set; } = new();
    public int TimeoutSec { get; set; } = 30;
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    public string NodeId { get; set; } = string.Empty;
    public string ProcessId { get; set; } = string.Empty;
    public int PersonaRetryCount { get; set; }
}

public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}
