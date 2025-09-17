using System;
using System.Collections.Generic;
using MediatR;
using Newtonsoft.Json.Linq;

namespace Aura.Orchestrator.Interventions;

public record CommandResult(bool Success, string? Error = null, object? Data = null)
{
    public static CommandResult Ok(object? data = null) => new(true, null, data);
    public static CommandResult Fail(string error) => new(false, error);
}

public sealed class ResumeOptions
{
    public IDroneCommand? ActionOverride { get; init; }
}

public interface IDroneCommand : IRequest<CommandResult>
{
    string CommandId { get; set; }
    JObject? Parameters { get; }
}

public abstract record DroneCommandBase : IDroneCommand
{
    protected DroneCommandBase()
    {
        CommandId = Guid.NewGuid().ToString("N");
        Parameters = new JObject();
    }

    public string CommandId { get; set; }

    public JObject? Parameters { get; init; }
}

public sealed record NavigateCommand : DroneCommandBase;

public sealed record TypeCommand : DroneCommandBase;

public sealed record ClickCommand : DroneCommandBase;

public sealed record WaitForElementCommand : DroneCommandBase;

public sealed record ExecuteScriptCommand : DroneCommandBase;

public sealed record ManageCookiesCommand : DroneCommandBase
{
    public CookieAction Action { get; init; }
}

public enum CookieAction
{
    Import,
    Export
}

public sealed class InterventionContext
{
    public string CommandId { get; set; } = string.Empty;
    public string ParentCommandId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public TimeSpan WindowTtl { get; set; }
    public TimeSpan StepTtl { get; set; }
    public DateTime LastStepTime { get; set; } = DateTime.UtcNow;
    public IDroneCommand ParentCommand { get; set; } = new NavigateCommand();
    public IDroneCommand? ReplayableAction { get; set; }
    public string? ScreenshotPath { get; set; }
    public string? Url { get; set; }
    public IDictionary<string, object?> DomContext { get; set; } = new Dictionary<string, object?>();
    public bool IsResumable { get; set; }
    public IList<InterventionStep> Steps { get; set; } = new List<InterventionStep>();
}

public sealed class InterventionStep
{
    public string CommandType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public IDroneCommand Command { get; set; } = new NavigateCommand();
}
