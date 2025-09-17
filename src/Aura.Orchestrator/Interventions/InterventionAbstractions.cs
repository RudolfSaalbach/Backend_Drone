using System.Collections.Generic;
using System.Threading;

namespace Aura.Orchestrator.Interventions;

public interface IBrowserController
{
    Task<string?> TakeScreenshotAsync(CancellationToken cancellationToken = default);
    Task<string?> GetCurrentUrlAsync(CancellationToken cancellationToken = default);
    void SetInteractionEnabled(bool enabled);
}

public interface IInterventionDetector
{
    Task<Dictionary<string, object?>> ExtractDomContextAsync(CancellationToken cancellationToken = default);
}

public sealed class InterventionOptions
{
    public bool AttachScreenshot { get; set; } = true;
    public int WindowTtlSec { get; set; } = 120;
    public int StepTtlSec { get; set; } = 30;
}
