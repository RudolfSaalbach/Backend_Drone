using Aura.Orchestrator.Models;

namespace Aura.Orchestrator.Interventions;

public interface IInterventionManager
{
    Task<InterventionContext> InitiateInterventionAsync(string reason, IDroneCommand parentCommand);

    Task<CommandResult> HandleInterventionCommandAsync(IDroneCommand command);

    Task<CommandResult> ResumeExecutionAsync(ResumeOptions? options = null);

    Task<bool> IsInInterventionModeAsync();

    InterventionContext? GetCurrentIntervention();

    Task<bool> CheckForInterventionAsync(string url, PersonaOverlay persona);
}
