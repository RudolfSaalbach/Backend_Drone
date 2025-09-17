using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Aura.Orchestrator.Communication;
using Aura.Orchestrator.Configuration;
using Aura.Orchestrator.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Aura.Orchestrator.Services;

public sealed record ResultPersistenceRequest(
    string CommandId,
    IReadOnlyList<ArtifactData> Artifacts,
    string? SessionLeaseId,
    JObject? SessionState);

public interface IResultPersistenceQueue
{
    ValueTask QueueAsync(ResultPersistenceRequest request, CancellationToken cancellationToken = default);
}

public sealed class ResultPersistenceQueue : BackgroundService, IResultPersistenceQueue
{
    private const int DefaultCapacity = 256;

    private readonly Channel<ResultPersistenceRequest> _queue;
    private readonly ISharedContextStore _sharedContextStore;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<ResultPersistenceQueue> _logger;

    public ResultPersistenceQueue(
        ISharedContextStore sharedContextStore,
        ISessionRegistry sessionRegistry,
        IMetricsCollector metrics,
        IOptions<OrchestratorConfig> options,
        ILogger<ResultPersistenceQueue> logger)
    {
        _sharedContextStore = sharedContextStore;
        _sessionRegistry = sessionRegistry;
        _metrics = metrics;
        _logger = logger;

        var configuredCapacity = Math.Max(DefaultCapacity, options.Value.Scheduling?.ReadyQueue?.Capacity ?? DefaultCapacity);
        var channelOptions = new BoundedChannelOptions(configuredCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _queue = Channel.CreateBounded<ResultPersistenceRequest>(channelOptions);
    }

    public ValueTask QueueAsync(ResultPersistenceRequest request, CancellationToken cancellationToken = default)
    {
        if (_queue.Writer.TryWrite(request))
        {
            return ValueTask.CompletedTask;
        }

        return _queue.Writer.WriteAsync(request, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProcessAsync(workItem, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(ResultPersistenceRequest request, CancellationToken cancellationToken)
    {
        if (request.Artifacts.Count > 0)
        {
            foreach (var artifact in request.Artifacts)
            {
                var artifactType = string.IsNullOrWhiteSpace(artifact.Type) ? "unknown" : artifact.Type;
                try
                {
                    switch (artifact.Type?.ToLowerInvariant())
                    {
                        case "facts" when artifact.Data is JArray facts:
                            await _sharedContextStore.StoreFactsAsync(facts, cancellationToken).ConfigureAwait(false);
                            break;
                        case "snippets" when artifact.Data is JArray snippets:
                            await _sharedContextStore.StoreSnippetsAsync(snippets, cancellationToken).ConfigureAwait(false);
                            break;
                        default:
                            await _sharedContextStore.StoreArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
                            break;
                    }

                    _metrics.IncrementCounter(
                        "artifacts_persisted_total",
                        1,
                        new Dictionary<string, string> { ["type"] = artifactType });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(
                        "Artifact persistence for command {CommandId} canceled during shutdown",
                        request.CommandId);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to persist artifact {ArtifactType} for command {CommandId}",
                        artifactType,
                        request.CommandId);
                    _metrics.IncrementCounter(
                        "artifacts_persist_failed_total",
                        1,
                        new Dictionary<string, string> { ["type"] = artifactType });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.SessionLeaseId) && request.SessionState is JObject state)
        {
            try
            {
                await _sessionRegistry.UpdateSessionStateAsync(request.SessionLeaseId!, state, cancellationToken).ConfigureAwait(false);
                _metrics.IncrementCounter("session_state_persisted_total");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "Session state persistence for command {CommandId} canceled during shutdown",
                    request.CommandId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to persist session state for lease {LeaseId} from command {CommandId}",
                    request.SessionLeaseId,
                    request.CommandId);
                _metrics.IncrementCounter("session_state_persist_failed_total");
            }
        }
    }
}
