using System.Threading;
using Newtonsoft.Json.Linq;

namespace Aura.Orchestrator.Services;

public interface ISessionRegistry
{
    Task UpdateSessionStateAsync(string leaseId, JObject state, CancellationToken cancellationToken = default);
}
