using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Aura.Orchestrator.Metrics;
using Aura.Orchestrator.Models;
using Newtonsoft.Json.Linq;

namespace Aura.Orchestrator.Interventions;

public sealed class EnhancedInterventionManager : IInterventionManager
{
    private readonly ILogger<EnhancedInterventionManager> _logger;
    private readonly IMediator _mediator;
    private readonly IBrowserController _browserController;
    private readonly IInterventionDetector _detector;
    private readonly IMetricsCollector _metricsCollector;
    private readonly InterventionOptions _options;

    private readonly SemaphoreSlim _interventionLock = new(1, 1);
    private InterventionContext? _currentIntervention;
    private CancellationTokenSource? _timeoutCts;
    private CancellationTokenSource? _stepTimeoutCts;

    public EnhancedInterventionManager(
        ILogger<EnhancedInterventionManager> logger,
        IMediator mediator,
        IBrowserController browserController,
        IInterventionDetector detector,
        IMetricsCollector metricsCollector,
        IOptions<InterventionOptions> options)
    {
        _logger = logger;
        _mediator = mediator;
        _browserController = browserController;
        _detector = detector;
        _metricsCollector = metricsCollector;
        _options = options.Value;
    }

    public async Task<InterventionContext> InitiateInterventionAsync(string reason, IDroneCommand parentCommand)
    {
        await _interventionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentIntervention != null)
            {
                throw new InvalidOperationException("An intervention is already active.");
            }

            _logger.LogInformation("Initiating intervention: {Reason} for command {CommandId}", reason, parentCommand.CommandId);

            string? screenshotPath = null;
            if (_options.AttachScreenshot)
            {
                screenshotPath = await _browserController.TakeScreenshotAsync().ConfigureAwait(false);
            }

            var currentUrl = await _browserController.GetCurrentUrlAsync().ConfigureAwait(false);
            var domContext = await _detector.ExtractDomContextAsync().ConfigureAwait(false);

            var replayableAction = CreateReplayableAction(parentCommand);

            _currentIntervention = new InterventionContext
            {
                CommandId = parentCommand.CommandId,
                ParentCommandId = parentCommand.CommandId,
                Reason = reason,
                StartTime = DateTime.UtcNow,
                WindowTtl = TimeSpan.FromSeconds(_options.WindowTtlSec),
                StepTtl = TimeSpan.FromSeconds(_options.StepTtlSec),
                ParentCommand = parentCommand,
                ReplayableAction = replayableAction,
                ScreenshotPath = screenshotPath,
                Url = currentUrl,
                DomContext = domContext,
                IsResumable = true,
                LastStepTime = DateTime.UtcNow
            };

            CancelWindowTimer();
            CancelStepTimer();
            _timeoutCts = new CancellationTokenSource(_currentIntervention.WindowTtl);
            _timeoutCts.Token.Register(() => _ = HandleInterventionTimeoutAsync());
            ResetStepTimer();

            _browserController.SetInteractionEnabled(true);

            _metricsCollector.IncrementCounter("drone_interventions_total", 1,
                new Dictionary<string, string> { ["reason"] = reason });

            await SendInterventionEventAsync().ConfigureAwait(false);

            return _currentIntervention;
        }
        finally
        {
            _interventionLock.Release();
        }
    }

    public async Task<CommandResult> HandleInterventionCommandAsync(IDroneCommand command)
    {
        await _interventionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentIntervention == null)
            {
                return CommandResult.Fail("not_in_intervention");
            }

            if (!string.Equals(command.Parameters?["mode"]?.ToString(), "intervention", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Command missing mode=intervention parameter");
                return CommandResult.Fail("invalid_in_intervention_mode");
            }

            if (!string.Equals(command.Parameters?["parentCommandId"]?.ToString(), _currentIntervention.ParentCommandId, StringComparison.Ordinal))
            {
                _logger.LogWarning("Command parentCommandId mismatch");
                return CommandResult.Fail("invalid_in_intervention_mode");
            }

            if (!IsAllowedInterventionCommand(command))
            {
                _logger.LogWarning("Command {CommandType} not permitted during intervention", command.GetType().Name);
                return CommandResult.Fail("invalid_in_intervention_mode");
            }

            _currentIntervention.LastStepTime = DateTime.UtcNow;
            _currentIntervention.Steps.Add(new InterventionStep
            {
                CommandType = command.GetType().Name,
                Timestamp = DateTime.UtcNow,
                Command = command
            });

            ResetStepTimer();

            _logger.LogDebug("Executing intervention command: {CommandType}", command.GetType().Name);
            return await _mediator.Send(command).ConfigureAwait(false);
        }
        finally
        {
            _interventionLock.Release();
        }
    }

    public async Task<CommandResult> ResumeExecutionAsync(ResumeOptions? options = null)
    {
        await _interventionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentIntervention == null)
            {
                return CommandResult.Fail("not_in_intervention");
            }

            _logger.LogInformation("Resuming execution after intervention");

            CancelWindowTimer();
            CancelStepTimer();

            _browserController.SetInteractionEnabled(false);

            var actionToReplay = options?.ActionOverride ?? _currentIntervention.ReplayableAction;
            if (actionToReplay != null)
            {
                _logger.LogInformation("Replaying action: {CommandType}", actionToReplay.GetType().Name);
                var replayResult = await _mediator.Send(actionToReplay).ConfigureAwait(false);
                if (!replayResult.Success)
                {
                    _logger.LogWarning("Replay action failed: {Error}", replayResult.Error);
                }
            }

            var duration = DateTime.UtcNow - _currentIntervention.StartTime;
            _metricsCollector.RecordHistogram("drone_intervention_window_ms", duration.TotalMilliseconds);

            var parentCommand = _currentIntervention.ParentCommand;
            _currentIntervention = null;

            return CommandResult.Ok(new
            {
                resumed = true,
                parentCommandId = parentCommand.CommandId,
                duration
            });
        }
        finally
        {
            _interventionLock.Release();
        }
    }

    public async Task<bool> IsInInterventionModeAsync()
    {
        await _interventionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _currentIntervention != null;
        }
        finally
        {
            _interventionLock.Release();
        }
    }

    public InterventionContext? GetCurrentIntervention() => _currentIntervention;

    public Task<bool> CheckForInterventionAsync(string url, PersonaOverlay persona)
    {
        persona ??= new PersonaOverlay();
        var requiresIntervention = ShouldTriggerIntervention(url, persona);
        if (requiresIntervention)
        {
            var personaId = string.IsNullOrWhiteSpace(persona.PersonaId) ? "unknown" : persona.PersonaId;
            _logger.LogInformation("Persona {PersonaId} flagged {Url} for manual intervention", personaId, url);
            _metricsCollector.IncrementCounter("drone_intervention_candidates", 1, new Dictionary<string, string>
            {
                ["persona_id"] = personaId,
                ["reason"] = "persona_rules"
            });
        }

        return Task.FromResult(requiresIntervention);
    }

    private bool ShouldTriggerIntervention(string url, PersonaOverlay persona)
    {
        if (string.IsNullOrWhiteSpace(url) || persona == null || persona.Traits == null)
        {
            return false;
        }

        var traits = persona.Traits;
        var affirmativeKeys = new[]
        {
            "requireIntervention",
            "requiresIntervention",
            "alwaysRequireIntervention",
            "manualReview",
            "manual_review",
            "forceIntervention"
        };

        foreach (var key in affirmativeKeys)
        {
            if (traits.TryGetValue(key, out var flag) && TryGetBool(flag))
            {
                return true;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        var path = uri.AbsolutePath;
        var fullUrl = uri.ToString();

        if (MatchesTrait(traits, "interventionDomains", host, static (pattern, candidate) => candidate.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (MatchesTrait(traits, "interventionPaths", path, static (pattern, candidate) => candidate.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        if (MatchesTrait(traits, "interventionKeywords", fullUrl, static (pattern, candidate) => candidate.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        if (traits.TryGetValue("interventionRules", out var rules) && EvaluateRuleCollection(rules, host, path, fullUrl))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesTrait(IDictionary<string, object?> traits, string key, string candidate, Func<string, string, bool> predicate)
    {
        if (!traits.TryGetValue(key, out var value))
        {
            return false;
        }

        foreach (var pattern in ExtractStringValues(value))
        {
            if (predicate(pattern, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateRuleCollection(object? rules, string host, string path, string fullUrl)
    {
        switch (rules)
        {
            case null:
                return false;
            case string single:
                return host.EndsWith(single, StringComparison.OrdinalIgnoreCase) ||
                       path.IndexOf(single, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       fullUrl.IndexOf(single, StringComparison.OrdinalIgnoreCase) >= 0;
            case JObject jObject:
                return EvaluateRuleCollection(jObject.ToObject<Dictionary<string, object?>>() ?? new Dictionary<string, object?>(), host, path, fullUrl);
            case IDictionary dictionary:
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key?.ToString();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var values = ExtractStringValues(entry.Value).ToArray();
                    if (values.Length == 0)
                    {
                        continue;
                    }

                    if (IsDomainKey(key) && values.Any(value => host.EndsWith(value, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    if (IsPathKey(key) && values.Any(value => path.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }

                    if (IsKeywordKey(key) && values.Any(value => fullUrl.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }
                }

                return false;
            }
            case IEnumerable enumerable when rules is not string:
            {
                foreach (var entry in enumerable)
                {
                    if (EvaluateRuleCollection(entry, host, path, fullUrl))
                    {
                        return true;
                    }
                }

                return false;
            }
            default:
                var fallback = rules.ToString();
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    return false;
                }

                return host.EndsWith(fallback, StringComparison.OrdinalIgnoreCase) ||
                       path.IndexOf(fallback, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       fullUrl.IndexOf(fallback, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    private static bool IsDomainKey(string key) => key.Equals("domain", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("domains", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("hosts", StringComparison.OrdinalIgnoreCase);

    private static bool IsPathKey(string key) => key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("paths", StringComparison.OrdinalIgnoreCase);

    private static bool IsKeywordKey(string key) => key.Equals("keyword", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("keywords", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("contains", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ExtractStringValues(object? raw)
    {
        switch (raw)
        {
            case null:
                yield break;
            case string single when !string.IsNullOrWhiteSpace(single):
                yield return single.Trim();
                yield break;
            case JValue jValue when jValue.Type == JTokenType.String:
                var text = jValue.Value<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text.Trim();
                }
                yield break;
            case JValue jValue when jValue.Type == JTokenType.Boolean:
                yield return jValue.Value<bool>().ToString();
                yield break;
            case IEnumerable enumerable when raw is not string:
                foreach (var item in enumerable)
                {
                    foreach (var value in ExtractStringValues(item))
                    {
                        yield return value;
                    }
                }
                yield break;
            default:
                var fallback = raw.ToString();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    yield return fallback.Trim();
                }
                yield break;
        }
    }

    private static bool TryGetBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        JValue jValue when jValue.Type == JTokenType.Boolean => jValue.Value<bool>(),
        JValue jValue when jValue.Type == JTokenType.Integer => jValue.Value<long>() != 0,
        _ => false
    };

    private void ResetStepTimer()
    {
        CancelStepTimer();

        if (_currentIntervention == null)
        {
            return;
        }

        if (_currentIntervention.StepTtl <= TimeSpan.Zero)
        {
            return;
        }

        var stepCts = new CancellationTokenSource();
        _stepTimeoutCts = stepCts;
        stepCts.CancelAfter(_currentIntervention.StepTtl);
        stepCts.Token.Register(() => _ = HandleStepTimeoutAsync());
    }

    private void CancelStepTimer()
    {
        var existing = Interlocked.Exchange(ref _stepTimeoutCts, null);
        if (existing != null)
        {
            try
            {
                existing.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
            existing.Dispose();
        }
    }

    private void CancelWindowTimer()
    {
        var existing = Interlocked.Exchange(ref _timeoutCts, null);
        if (existing != null)
        {
            try
            {
                existing.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
            existing.Dispose();
        }
    }

    private Task HandleStepTimeoutAsync()
    {
        _logger.LogWarning("Intervention step timeout for command {CommandId}", _currentIntervention?.CommandId);
        return Task.Run(async () =>
        {
            await _interventionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_currentIntervention != null)
                {
                    var elapsed = DateTime.UtcNow - _currentIntervention.LastStepTime;
                    if (_currentIntervention.StepTtl > TimeSpan.Zero && elapsed >= _currentIntervention.StepTtl)
                    {
                        var commandId = _currentIntervention.CommandId;
                        _currentIntervention = null;
                        CancelWindowTimer();
                        CancelStepTimer();
                        _browserController.SetInteractionEnabled(false);
                        _metricsCollector.IncrementCounter("drone_intervention_step_timeouts", 1);
                        _logger.LogWarning("Intervention cancelled due to step TTL for command {CommandId}", commandId);
                    }
                }
            }
            finally
            {
                _interventionLock.Release();
            }
        });
    }

    private static bool IsAllowedInterventionCommand(IDroneCommand command) => command switch
    {
        NavigateCommand => true,
        TypeCommand => true,
        ClickCommand => true,
        WaitForElementCommand => true,
        ExecuteScriptCommand script when script.Parameters?["safe"]?.ToObject<bool>() == true => true,
        ManageCookiesCommand cookie when cookie.Action is CookieAction.Export or CookieAction.Import => true,
        var c when c.GetType().Name.Contains("Wait", StringComparison.OrdinalIgnoreCase) => true,
        var c when c.GetType().Name.Contains("Scroll", StringComparison.OrdinalIgnoreCase) => true,
        var c when c.GetType().Name.Contains("MouseMove", StringComparison.OrdinalIgnoreCase) => true,
        _ => false
    };

    private IDroneCommand? CreateReplayableAction(IDroneCommand original)
    {
        var json = JsonConvert.SerializeObject(original);
        var clone = JsonConvert.DeserializeObject(json, original.GetType()) as IDroneCommand;
        if (clone != null)
        {
            clone.CommandId = $"{original.CommandId}_replay";
        }

        return clone;
    }

    private async Task SendInterventionEventAsync()
    {
        if (_currentIntervention == null)
        {
            return;
        }

        var payload = new
        {
            @event = "RequireIntervention",
            commandId = _currentIntervention.CommandId,
            parentCommandId = _currentIntervention.ParentCommandId,
            reason = _currentIntervention.Reason,
            resumable = true,
            context = _currentIntervention.DomContext,
            screenshot = _currentIntervention.ScreenshotPath != null
                ? new { path = _currentIntervention.ScreenshotPath }
                : null
        };

        _logger.LogDebug("Sending intervention event: {Payload}", JsonConvert.SerializeObject(payload));
        await Task.CompletedTask;
    }

    private Task HandleInterventionTimeoutAsync()
    {
        _logger.LogWarning("Intervention timeout for command {CommandId}", _currentIntervention?.CommandId);
        return Task.Run(async () =>
        {
            await _interventionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_currentIntervention != null)
                {
                    var timedOutCommand = _currentIntervention.CommandId;
                    _currentIntervention = null;
                    CancelStepTimer();
                    CancelWindowTimer();
                    _browserController.SetInteractionEnabled(false);
                    _metricsCollector.IncrementCounter("drone_intervention_timeouts", 1);
                    _logger.LogWarning("Intervention timed out after window TTL for command {CommandId}", timedOutCommand);
                }
            }
            finally
            {
                _interventionLock.Release();
            }
        });
    }
}
