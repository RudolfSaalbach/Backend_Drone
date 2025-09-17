using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Aura.Orchestrator.Models;

namespace Aura.Orchestrator.Communication;

public sealed class CommandPayload
{
    [JsonProperty("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("parameters")]
    public JObject Parameters { get; set; } = new();

    [JsonProperty("persona")]
    public PersonaData Persona { get; set; } = new();

    [JsonProperty("session")]
    public SessionInfo Session { get; set; } = new();

    [JsonProperty("timeoutSec")]
    public int TimeoutSec { get; set; }
}

public sealed class QueryPayload
{
    [JsonProperty("queryId")]
    public string QueryId { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("parameters")]
    public JObject Parameters { get; set; } = new();
}

public sealed class CommandResultPayload
{
    [JsonProperty("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [JsonProperty("result")]
    public JObject Result { get; set; } = new();

    [JsonProperty("artifacts")]
    public IList<ArtifactData> Artifacts { get; set; } = new List<ArtifactData>();

    [JsonProperty("sessionLeaseId")]
    public string SessionLeaseId { get; set; } = string.Empty;

    [JsonProperty("sessionState")]
    public JObject SessionState { get; set; } = new();
}

public sealed class CommandErrorPayload
{
    [JsonProperty("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [JsonProperty("error")]
    public string Error { get; set; } = string.Empty;

    [JsonProperty("errorType")]
    public string ErrorType { get; set; } = string.Empty;

    [JsonProperty("canRetry")]
    public bool CanRetry { get; set; }
}

public sealed class StatusPayload
{
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("currentCommand")]
    public string CurrentCommand { get; set; } = string.Empty;

    [JsonProperty("progress")]
    public int Progress { get; set; }

    [JsonProperty("memoryUsage")]
    public long MemoryUsage { get; set; }

    [JsonProperty("cpuUsage")]
    public double CpuUsage { get; set; }
}

public sealed class InterventionPayload
{
    [JsonProperty("commandId")]
    public string CommandId { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("data")]
    public JObject Data { get; set; } = new();

    [JsonProperty("resumeToken")]
    public string ResumeToken { get; set; } = string.Empty;
}

public sealed class QueryResponsePayload
{
    [JsonProperty("queryId")]
    public string QueryId { get; set; } = string.Empty;

    [JsonProperty("response")]
    public JObject Response { get; set; } = new();
}

public sealed class ArtifactData
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("data")]
    public JToken? Data { get; set; }

    [JsonProperty("metadata")]
    public JObject Metadata { get; set; } = new();
}
