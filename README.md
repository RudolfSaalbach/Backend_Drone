# Backend Drone Orchestrator

This repository contains a modular backend foundation for coordinating browser automation drones. It is organised as a reusable .NET class library and a set of standalone JavaScript utilities for the drone runtime.

## Projects

- `src/Aura.Orchestrator` – core orchestrator components including the SignalR hub, scheduler, domain limiter, intervention manager and shared models.
- `scripts/` – human-like browser interaction helpers and detection modules that can be loaded dynamically by drones.

## Highlights

- **Fair scheduling:** `OrchestratorTaskScheduler` distributes work using bounded queues, weighted selection and pace tokens per drone.
- **Command lifecycle tracking:** `CommandLifecycleTracker` owns pacing semaphores and domain leases, releasing them automatically on acknowledgement, completion or failure.
- **Domain guard:** `DomainLimiter` enforces concurrency, QPS and burst cooldowns per drone and globally using a maintained public suffix list.
- **SignalR hub:** `DroneHub` authenticates drones, routes events and persists artefacts while updating metrics.
- **Intervention workflow:** `EnhancedInterventionManager` provides replayable intervention sessions with timeout handling.
- **Human-like scripts:** Modernised detection and action scripts avoid invalid selectors and expose ES module exports.

## Building

1. Install the .NET 8 SDK.
2. Restore and build the class library:
   ```bash
   dotnet restore src/Aura.Orchestrator/Aura.Orchestrator.csproj
   dotnet build src/Aura.Orchestrator/Aura.Orchestrator.csproj
   ```

The class library is ready to be consumed by an ASP.NET Core host or worker service. Integration tests can target the exposed abstractions.
