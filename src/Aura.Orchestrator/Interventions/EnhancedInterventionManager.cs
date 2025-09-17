using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Aura.Orchestrator.Metrics;

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

            _timeoutCts = new CancellationTokenSource(_currentIntervention.WindowTtl);
            _timeoutCts.Token.Register(() => _ = HandleInterventionTimeoutAsync());

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

            _timeoutCts?.Cancel();
            _timeoutCts = null;

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
        _ = url;
        _ = persona;
        return Task.FromResult(false);
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
                    _currentIntervention = null;
                    _browserController.SetInteractionEnabled(false);
                    _metricsCollector.IncrementCounter("drone_intervention_timeouts", 1);
                }
            }
            finally
            {
                _interventionLock.Release();
            }
        });
    }
}
