using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Aura.Orchestrator.Security;

public interface IPreciseDomainMatcher
{
    bool IsAllowed(string url, IReadOnlyCollection<string>? allowList, IReadOnlyCollection<string>? denyList);
    string? GetRegistrableDomain(string url);
}

public sealed class PreciseDomainMatcher : IPreciseDomainMatcher
{
    private readonly ILogger<PreciseDomainMatcher> _logger;
    private readonly PublicSuffixList _publicSuffixList;

    public PreciseDomainMatcher(ILogger<PreciseDomainMatcher> logger, PublicSuffixList publicSuffixList)
    {
        _logger = logger;
        _publicSuffixList = publicSuffixList;
    }

    public bool IsAllowed(string url, IReadOnlyCollection<string>? allowList, IReadOnlyCollection<string>? denyList)
    {
        var domain = GetRegistrableDomain(url);
        if (string.IsNullOrEmpty(domain))
        {
            _logger.LogDebug("Rejecting {Url} because no registrable domain could be derived", url);
            return false;
        }

        if (denyList != null)
        {
            foreach (var rule in denyList)
            {
                if (IsMatchOrSubdomain(domain, rule))
                {
                    _logger.LogDebug("Domain {Domain} blocked by deny rule {Rule}", domain, rule);
                    return false;
                }
            }
        }

        if (allowList == null || allowList.Count == 0)
        {
            return true;
        }

        foreach (var rule in allowList)
        {
            if (IsMatchOrSubdomain(domain, rule))
            {
                return true;
            }
        }

        _logger.LogDebug("Domain {Domain} not covered by allow-list", domain);
        return false;
    }

    public string? GetRegistrableDomain(string url)
    {
        var registrable = _publicSuffixList.GetRegistrableDomain(url);
        if (registrable == null)
        {
            _logger.LogDebug("Unable to parse registrable domain from {Url}", url);
        }

        return registrable;
    }

    private static bool IsMatchOrSubdomain(string domain, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        if (string.Equals(domain, rule, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return domain.EndsWith($".{rule}", StringComparison.OrdinalIgnoreCase);
    }
}
