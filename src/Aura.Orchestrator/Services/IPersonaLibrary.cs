using System.Threading;
using Aura.Orchestrator.Models;

namespace Aura.Orchestrator.Services;

public interface IPersonaLibrary
{
    Task<PersonaData?> LoadPersonaAsync(string personaId, CancellationToken cancellationToken = default);
}
