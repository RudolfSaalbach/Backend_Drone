using System.Threading;
using Newtonsoft.Json.Linq;
using Aura.Orchestrator.Communication;

namespace Aura.Orchestrator.Services;

public interface ISharedContextStore
{
    Task StoreFactsAsync(JArray facts, CancellationToken cancellationToken = default);

    Task StoreSnippetsAsync(JArray snippets, CancellationToken cancellationToken = default);

    Task StoreArtifactAsync(ArtifactData artifact, CancellationToken cancellationToken = default);
}
