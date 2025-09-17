using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aura.Orchestrator.Security;

public sealed class PublicSuffixList
{
    private readonly ILogger<PublicSuffixList> _logger;
    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcardRules;
    private readonly HashSet<string> _exceptionRules;

    public PublicSuffixList()
        : this(NullLogger<PublicSuffixList>.Instance)
    {
    }

    public PublicSuffixList(ILogger<PublicSuffixList> logger)
        : this(logger, LoadEmbeddedRules(logger))
    {
    }

    public PublicSuffixList(IEnumerable<string> rules)
        : this(NullLogger<PublicSuffixList>.Instance, rules)
    {
    }

    public PublicSuffixList(ILogger<PublicSuffixList> logger, IEnumerable<string> rules)
    {
        _logger = logger ?? NullLogger<PublicSuffixList>.Instance;
        _rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _wildcardRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _exceptionRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rules)
        {
            var rule = raw?.Trim();
            if (string.IsNullOrEmpty(rule) || rule.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.StartsWith("!", StringComparison.Ordinal))
            {
                _exceptionRules.Add(rule[1..]);
            }
            else if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                _wildcardRules.Add(rule[2..]);
            }
            else
            {
                _rules.Add(rule);
            }
        }

        if (_rules.Count == 0 && _wildcardRules.Count == 0 && _exceptionRules.Count == 0)
        {
            _logger.LogWarning("Public suffix list initialised with zero entries; applying default fallback rules.");
            foreach (var fallback in DefaultRules())
            {
                _rules.Add(fallback);
            }
        }
    }

    public string? GetRegistrableDomain(string url)
    {
        var host = ExtractHost(url);
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return host;
        }

        var idn = new IdnMapping();
        var asciiHost = idn.GetAscii(host);
        var labels = asciiHost.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0)
        {
            return null;
        }

        var match = FindMatchingRule(labels);
        var suffixLabelCount = match.LabelCount;
        if (suffixLabelCount >= labels.Length)
        {
            return asciiHost.ToLowerInvariant();
        }

        var registrableLabels = labels.Skip(labels.Length - suffixLabelCount - 1);
        var registrable = string.Join('.', registrableLabels).ToLowerInvariant();
        return registrable;
    }

    private (string Rule, int LabelCount, bool IsException) FindMatchingRule(string[] labels)
    {
        string? matchingRule = null;
        int matchingLabelCount = 0;

        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = Join(labels, i);
            if (_exceptionRules.Contains(candidate))
            {
                return (candidate, candidate.Split('.').Length - 1, true);
            }

            if (_rules.Contains(candidate))
            {
                var length = candidate.Split('.').Length;
                if (length > matchingLabelCount)
                {
                    matchingRule = candidate;
                    matchingLabelCount = length;
                }
            }

            if (i + 1 >= labels.Length)
            {
                continue;
            }

            var wildcardCandidate = Join(labels, i + 1);
            if (_wildcardRules.Contains(wildcardCandidate))
            {
                var length = wildcardCandidate.Split('.').Length + 1;
                if (length > matchingLabelCount)
                {
                    matchingRule = wildcardCandidate;
                    matchingLabelCount = length;
                }
            }
        }

        if (matchingRule == null)
        {
            return ("*", 1, false);
        }

        return (matchingRule, matchingLabelCount, false);
    }

    private static string Join(string[] labels, int startIndex)
    {
        return string.Join('.', labels.Skip(startIndex));
    }

    private static string? ExtractHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.Host;
        }

        if (Uri.TryCreate($"http://{url}", UriKind.Absolute, out var fallback))
        {
            return fallback.Host;
        }

        return null;
    }

    private static IEnumerable<string> LoadEmbeddedRules(ILogger<PublicSuffixList> logger)
    {
        if (TryLoadExternalRules(logger, out var externalRules))
        {
            return externalRules;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Aura.Orchestrator.Security.public_suffix_list.dat";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            logger.LogCritical("Embedded public suffix list resource was not found; configure PUBLIC_SUFFIX_LIST_PATH to restore full coverage.");
            return DefaultRules();
        }

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line != null)
            {
                lines.Add(line);
            }
        }

        if (lines.Count < 100)
        {
            logger.LogCritical("Embedded public suffix list contained only {Count} entries; configure an external dataset for accuracy.", lines.Count);
            return DefaultRules();
        }

        return lines;
    }

    private static bool TryLoadExternalRules(ILogger<PublicSuffixList> logger, out IEnumerable<string> rules)
    {
        var candidates = new List<string>();
        var envPath = Environment.GetEnvironmentVariable("PUBLIC_SUFFIX_LIST_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            candidates.Add(envPath);
        }

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "public_suffix_list.dat"));
        candidates.Add(Path.Combine(baseDir, "data", "public_suffix_list.dat"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var lines = File.ReadAllLines(candidate)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();

                if (lines.Count < 100)
                {
                    logger.LogWarning("Public suffix dataset at {Path} looked invalid (only {Count} entries)", candidate, lines.Count);
                    continue;
                }

                logger.LogInformation("Loaded public suffix list from external path {Path}", candidate);
                rules = lines;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load public suffix list from {Path}", candidate);
            }
        }

        rules = Array.Empty<string>();
        return false;
    }

    private static IEnumerable<string> DefaultRules()
    {
        return new[]
        {
            "com",
            "net",
            "org",
            "co.uk",
            "uk"
        };
    }
}
