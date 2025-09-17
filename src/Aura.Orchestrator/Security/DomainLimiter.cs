using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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
    private readonly TimeSpan _stateTtl;
    private readonly TimeSpan _cleanupInterval;
    private long _lastCleanupTicks;

    public DomainLimiter(ILogger<DomainLimiter> logger, IOptions<OrchestratorConfig> options, IMetricsCollector metrics)
    {
        _logger = logger;
        _config = options.Value;
        _metrics = metrics;
        var ttlSeconds = Math.Max(30, _config.Limits.DomainStateTtlSeconds);
        _stateTtl = TimeSpan.FromSeconds(ttlSeconds);
        _cleanupInterval = TimeSpan.FromSeconds(Math.Max(5, Math.Min(ttlSeconds / 4.0, 60)));
        _lastCleanupTicks = DateTime.UtcNow.Ticks;
    }

    public Task<DomainLease?> TryAcquireAsync(string droneId, string? domain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        var acquired = false;
        var throttled = false;
        string? throttleReason = null;

        lock (globalState.SyncRoot)
        {
            lock (droneState.SyncRoot)
            {
                droneState.TrimOldRequests(now);
                droneState.Touch(now);
                globalState.Touch(now);

                if (droneState.IsInCooldown(now))
                {
                    throttled = true;
                    throttleReason = "cooldown";
                }

                if (globalLimits.MaxConcurrentSessions > 0 &&
                    globalState.CurrentConcurrency >= globalLimits.MaxConcurrentSessions)
                {
                    _logger.LogDebug("Global concurrency limit reached for {Domain}", normalizedDomain);
                    throttled = true;
                    throttleReason = "global_concurrency";
                }

                if (perDomain.ConcurrencyPerDrone > 0 &&
                    droneState.CurrentConcurrency >= perDomain.ConcurrencyPerDrone)
                {
                    _logger.LogDebug("Drone {DroneId} at concurrency limit for {Domain}", droneId, normalizedDomain);
                    throttled = true;
                    throttleReason = "per_drone_concurrency";
                }

                if (perDomain.QpsPerDrone > 0 &&
                    droneState.CurrentQps(now) >= perDomain.QpsPerDrone)
                {
                    _logger.LogDebug("Drone {DroneId} would exceed QPS limit for {Domain}", droneId, normalizedDomain);
                    throttled = true;
                    throttleReason = "per_drone_qps";
                }

                if (!throttled)
                {
                    droneState.RecordRequest(now, perDomain);
                    globalState.CurrentConcurrency++;
                    droneState.CurrentConcurrency++;
                    acquired = true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!acquired)
        {
            if (throttled && throttleReason == "cooldown")
            {
                _logger.LogDebug("Drone {DroneId} is cooling down for {Domain}", droneId, normalizedDomain);
            }

            MaybeCleanup(now);
            return Task.FromResult<DomainLease?>(null);
        }

        _metrics.RecordGauge("domain_sessions_active", globalState.CurrentConcurrency,
            new Dictionary<string, string> { ["domain"] = normalizedDomain });

        var lease = new DomainLease(droneId, normalizedDomain, () => ReleaseInternal(droneId, normalizedDomain));
        MaybeCleanup(now);
        return Task.FromResult<DomainLease?>(lease);
    }

    private void ReleaseInternal(string droneId, string domain)
    {
        var now = DateTime.UtcNow;
        if (_globalStates.TryGetValue(domain, out var globalState))
        {
            lock (globalState.SyncRoot)
            {
                if (globalState.CurrentConcurrency > 0)
                {
                    globalState.CurrentConcurrency--;
                }

                globalState.Touch(now);
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

                droneState.Touch(now);
            }

            if (droneState.IsDormant(now, _stateTtl))
            {
                droneStates.TryRemove(domain, out _);
            }

            if (droneStates.IsEmpty)
            {
                _perDroneStates.TryRemove(droneId, out _);
            }
        }

        MaybeCleanup(now);
    }

    private sealed class GlobalDomainState
    {
        public object SyncRoot { get; } = new();
        public int CurrentConcurrency { get; set; }
        public DateTime LastTouchedUtc { get; private set; } = DateTime.UtcNow;

        public void Touch(DateTime timestamp)
        {
            LastTouchedUtc = timestamp;
        }

        public bool IsDormant(DateTime now, TimeSpan ttl) => CurrentConcurrency == 0 && now - LastTouchedUtc >= ttl;
    }

    private sealed class DroneDomainState
    {
        private readonly Queue<DateTime> _requestTimes = new();
        private readonly Queue<DateTime> _burstRequestTimes = new();
        private DateTime _cooldownUntil = DateTime.MinValue;

        public object SyncRoot { get; } = new();
        public int CurrentConcurrency { get; set; }
        public DateTime LastTouchedUtc { get; private set; } = DateTime.UtcNow;

        public void TrimOldRequests(DateTime now)
        {
            while (_requestTimes.Count > 0 && (now - _requestTimes.Peek()).TotalSeconds > 1)
            {
                _requestTimes.Dequeue();
            }

            LastTouchedUtc = now;
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
            LastTouchedUtc = timestamp;

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

        public void Touch(DateTime timestamp)
        {
            LastTouchedUtc = timestamp;
        }

        public bool IsDormant(DateTime now, TimeSpan ttl)
        {
            return CurrentConcurrency == 0 && now - LastTouchedUtc >= ttl && _requestTimes.Count == 0 && _burstRequestTimes.Count == 0;
        }
    }

    private void MaybeCleanup(DateTime now)
    {
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (now.Ticks - last < _cleanupInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupTicks, now.Ticks, last) != last)
        {
            return;
        }

        CleanupDormantStates(now);
    }

    private void CleanupDormantStates(DateTime now)
    {
        foreach (var entry in _globalStates)
        {
            if (entry.Value.IsDormant(now, _stateTtl))
            {
                _globalStates.TryRemove(entry.Key, out _);
            }
        }

        foreach (var droneEntry in _perDroneStates)
        {
            var stateMap = droneEntry.Value;
            foreach (var domainEntry in stateMap)
            {
                if (domainEntry.Value.IsDormant(now, _stateTtl))
                {
                    stateMap.TryRemove(domainEntry.Key, out _);
                }
            }

            if (stateMap.IsEmpty)
            {
                _perDroneStates.TryRemove(droneEntry.Key, out _);
            }
        }
    }
}
