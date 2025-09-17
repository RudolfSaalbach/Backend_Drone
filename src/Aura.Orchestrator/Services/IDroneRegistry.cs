using System.Collections.Generic;
using System.Threading;
using Aura.Orchestrator.Communication;
using Aura.Orchestrator.Models;

namespace Aura.Orchestrator.Services;

public interface IDroneRegistry
{
    Task<IReadOnlyCollection<DroneInfo>> GetActiveDronesAsync(CancellationToken cancellationToken = default);

    Task<DroneInfo?> GetDroneAsync(string droneId, CancellationToken cancellationToken = default);

    Task RegisterDroneAsync(DroneInfo drone, CancellationToken cancellationToken = default);

    Task UnregisterDroneAsync(string droneId, CancellationToken cancellationToken = default);

    Task UpdateDroneStatusAsync(string droneId, StatusPayload status, CancellationToken cancellationToken = default);

    Task UpdateHeartbeatAsync(string droneId, CancellationToken cancellationToken = default);

    Task IncrementDroneErrorCountAsync(string droneId, CancellationToken cancellationToken = default);

    string? GetDroneIdByConnection(string connectionId);
}
