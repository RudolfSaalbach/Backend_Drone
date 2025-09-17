using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Aura.Orchestrator.Security;

public sealed class PublicSuffixList
{
    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcardRules;
    private readonly HashSet<string> _exceptionRules;

    public PublicSuffixList()
        : this(LoadEmbeddedRules())
    {
    }

    public PublicSuffixList(IEnumerable<string> rules)
    {
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
        string asciiHost;
        try
        {
            asciiHost = idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            return host.ToLowerInvariant();
        }
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

    private static IEnumerable<string> LoadEmbeddedRules()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Aura.Orchestrator.Security.public_suffix_list.dat";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
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

        return lines;
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
