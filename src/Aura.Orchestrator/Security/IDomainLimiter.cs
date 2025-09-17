using System;
using System.Threading;

namespace Aura.Orchestrator.Security;

public interface IDomainLimiter
{
    Task<DomainLease?> TryAcquireAsync(string droneId, string? domain, CancellationToken cancellationToken);
}

public sealed class DomainLease : IDisposable
{
    private readonly Action _releaser;
    private bool _released;

    internal DomainLease(string droneId, string domain, Action releaser)
    {
        DroneId = droneId;
        Domain = domain;
        _releaser = releaser;
    }

    public string DroneId { get; }

    public string Domain { get; }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _releaser();
    }
}
