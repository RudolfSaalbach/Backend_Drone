using Newtonsoft.Json;

namespace Aura.Orchestrator.Models;

public sealed class PersonaData
{
    [JsonProperty("personaId")]
    public string PersonaId { get; set; } = string.Empty;

    [JsonProperty("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonProperty("humanLike")]
    public HumanLikeBehavior HumanLike { get; set; } = new();
}

public sealed class HumanLikeBehavior
{
    [JsonProperty("typing")]
    public TypingProfile Typing { get; set; } = new();

    [JsonProperty("click")]
    public ClickProfile Click { get; set; } = new();

    [JsonProperty("scroll")]
    public ScrollProfile Scroll { get; set; } = new();

    [JsonProperty("mouseMove")]
    public MouseMoveProfile MouseMove { get; set; } = new();
}

public sealed class TypingProfile
{
    [JsonProperty("clearFirst")]
    public bool ClearFirst { get; set; }

    [JsonProperty("charDelayMs")]
    public int CharDelayMs { get; set; } = 120;

    [JsonProperty("varianceMs")]
    public int VarianceMs { get; set; } = 45;

    [JsonProperty("maxDelayMs")]
    public int MaxDelayMs { get; set; } = 4000;
}

public sealed class ClickProfile
{
    [JsonProperty("preMoveDelayMs")]
    public int PreMoveDelayMs { get; set; } = 150;

    [JsonProperty("hoverMs")]
    public int HoverMs { get; set; } = 100;
}

public sealed class ScrollProfile
{
    [JsonProperty("directionDefault")]
    public string DirectionDefault { get; set; } = "down";

    [JsonProperty("chunkPx")]
    public int ChunkPx { get; set; } = 280;

    [JsonProperty("chunks")]
    public int Chunks { get; set; } = 3;

    [JsonProperty("delayMs")]
    public int DelayMs { get; set; } = 180;

    [JsonProperty("varianceMs")]
    public int VarianceMs { get; set; } = 60;
}

public sealed class MouseMoveProfile
{
    [JsonProperty("enable")]
    public bool Enable { get; set; }

    [JsonProperty("hoverMs")]
    public int HoverMs { get; set; } = 120;
}

public sealed class PersonaOverlay
{
    public string PersonaId { get; set; } = string.Empty;
    public IDictionary<string, object?> Traits { get; set; } = new Dictionary<string, object?>();
}

public sealed class SessionInfo
{
    [JsonProperty("lease_id")]
    public string LeaseId { get; set; } = string.Empty;

    [JsonProperty("site")]
    public string Site { get; set; } = string.Empty;

    [JsonProperty("identity")]
    public string Identity { get; set; } = string.Empty;
}
