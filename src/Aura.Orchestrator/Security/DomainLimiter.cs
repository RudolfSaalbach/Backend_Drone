using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aura.Orchestrator.Configuration;
using Aura.Orchestrator.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aura.Orchestrator.Security;

public sealed class DomainLimiter : IDomainLimiter
{
    private readonly ILogger<DomainLimiter> _logger;
    private readonly OrchestratorConfig _config;
    private readonly IMetricsCollector _metrics;
    private readonly ConcurrentDictionary<string, GlobalDomainState> _globalStates = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DroneDomainState>> _perDroneStates = new();

    public DomainLimiter(ILogger<DomainLimiter> logger, IOptions<OrchestratorConfig> options, IMetricsCollector metrics)
    {
        _logger = logger;
        _config = options.Value;
        _metrics = metrics;
    }

    public Task<DomainLease?> TryAcquireAsync(string droneId, string? domain, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<DomainLease?>(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return Task.FromResult<DomainLease?>(null);
        }

        var normalizedDomain = domain.ToLowerInvariant();
        var globalState = _globalStates.GetOrAdd(normalizedDomain, static _ => new GlobalDomainState());
        var droneStates = _perDroneStates.GetOrAdd(droneId, static _ => new ConcurrentDictionary<string, DroneDomainState>());
        var droneState = droneStates.GetOrAdd(normalizedDomain, static _ => new DroneDomainState());

        var now = DateTime.UtcNow;
        var perDomain = _config.Limits.PerDomain;
        var globalLimits = _config.Limits.Global;

        DomainLease? lease = null;
        bool acquired = false;

        lock (globalState.SyncRoot)
        {
            lock (droneState.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                droneState.TrimOldRequests(now);

                if (droneState.IsInCooldown(now))
                {
                    return Task.FromResult<DomainLease?>(null);
                }

                if (globalLimits.MaxConcurrentSessions > 0 &&
                    globalState.CurrentConcurrency >= globalLimits.MaxConcurrentSessions)
                {
                    _logger.LogDebug("Global concurrency limit reached for {Domain}", normalizedDomain);
                    return Task.FromResult<DomainLease?>(null);
                }

                if (perDomain.ConcurrencyPerDrone > 0 &&
                    droneState.CurrentConcurrency >= perDomain.ConcurrencyPerDrone)
                {
                    _logger.LogDebug("Drone {DroneId} at concurrency limit for {Domain}", droneId, normalizedDomain);
                    return Task.FromResult<DomainLease?>(null);
                }

                if (perDomain.QpsPerDrone > 0 &&
                    droneState.CurrentQps(now) >= perDomain.QpsPerDrone)
                {
                    _logger.LogDebug("Drone {DroneId} would exceed QPS limit for {Domain}", droneId, normalizedDomain);
                    return Task.FromResult<DomainLease?>(null);
                }

                droneState.RecordRequest(now, perDomain);
                globalState.CurrentConcurrency++;
                droneState.CurrentConcurrency++;
                acquired = true;
            }
        }

        if (!acquired)
        {
            return Task.FromResult<DomainLease?>(null);
        }

        _metrics.RecordGauge("domain_sessions_active", globalState.CurrentConcurrency,
            new Dictionary<string, string> { ["domain"] = normalizedDomain });

        lease = new DomainLease(droneId, normalizedDomain, () => ReleaseInternal(droneId, normalizedDomain));
        return Task.FromResult<DomainLease?>(lease);
    }

    private void ReleaseInternal(string droneId, string domain)
    {
        if (_globalStates.TryGetValue(domain, out var globalState))
        {
            lock (globalState.SyncRoot)
            {
                if (globalState.CurrentConcurrency > 0)
                {
                    globalState.CurrentConcurrency--;
                }

                _metrics.RecordGauge("domain_sessions_active", globalState.CurrentConcurrency,
                    new Dictionary<string, string> { ["domain"] = domain });
            }
        }

        if (_perDroneStates.TryGetValue(droneId, out var droneStates) &&
            droneStates.TryGetValue(domain, out var droneState))
        {
            lock (droneState.SyncRoot)
            {
                if (droneState.CurrentConcurrency > 0)
                {
                    droneState.CurrentConcurrency--;
                }
            }
        }
    }

    private sealed class GlobalDomainState
    {
        public object SyncRoot { get; } = new();
        public int CurrentConcurrency { get; set; }
    }

    private sealed class DroneDomainState
    {
        private readonly Queue<DateTime> _requestTimes = new();
        private readonly Queue<DateTime> _burstRequestTimes = new();
        private DateTime _cooldownUntil = DateTime.MinValue;

        public object SyncRoot { get; } = new();
        public int CurrentConcurrency { get; set; }

        public void TrimOldRequests(DateTime now)
        {
            while (_requestTimes.Count > 0 && (now - _requestTimes.Peek()).TotalSeconds > 1)
            {
                _requestTimes.Dequeue();
            }
        }

        public double CurrentQps(DateTime now)
        {
            TrimOldRequests(now);
            return _requestTimes.Count;
        }

        public bool IsInCooldown(DateTime now) => now < _cooldownUntil;

        public void RecordRequest(DateTime timestamp, PerDomainLimits limits)
        {
            _requestTimes.Enqueue(timestamp);

            if (limits.BurstLimit > 0)
            {
                var windowSeconds = Math.Max(1, limits.CooldownSeconds);
                var window = TimeSpan.FromSeconds(windowSeconds);

                while (_burstRequestTimes.Count > 0 && timestamp - _burstRequestTimes.Peek() > window)
                {
                    _burstRequestTimes.Dequeue();
                }

                _burstRequestTimes.Enqueue(timestamp);

                if (_burstRequestTimes.Count >= limits.BurstLimit)
                {
                    _cooldownUntil = timestamp.AddSeconds(windowSeconds);
                    _burstRequestTimes.Clear();
                }
            }
        }
    }
}
