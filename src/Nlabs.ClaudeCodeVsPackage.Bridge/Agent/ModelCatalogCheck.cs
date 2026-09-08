using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>What the CLI knows versus what the panel offers.</summary>
public sealed class ModelCatalogDiff
{
    public ModelCatalogDiff(IReadOnlyList<string> newInCli, IReadOnlyList<string> missingFromCli)
    {
        NewInCli = newInCli;
        MissingFromCli = missingFromCli;
    }

    /// <summary>Aliases the CLI advertises that the panel does not offer - probably a new tier.</summary>
    public IReadOnlyList<string> NewInCli { get; }

    /// <summary>Aliases the panel offers that the CLI no longer mentions - probably retired.</summary>
    public IReadOnlyList<string> MissingFromCli { get; }

    /// <summary>True when the panel's model list has fallen behind the CLI.</summary>
    public bool IsStale => NewInCli.Count > 0;
}

/// <summary>
/// Notices when the panel's model list has gone stale. A hard-coded model list is a slow leak: a new
/// tier ships (fable did), the panel keeps offering yesterday's options, and nothing ever says so.
///
/// The CLI's own <c>--model</c> help is the source of truth - it names the aliases it accepts, e.g.
/// "Provide an alias for the latest model (e.g. 'fable', 'opus', or 'sonnet')". Full model ids
/// (<c>claude-...</c>) are examples, not aliases, so they are skipped. Pure and dependency-free: the
/// caller runs the process, this only reads the text.
/// </summary>
public static class ModelCatalogCheck
{
    private static readonly Regex Quoted = new Regex(@"'([^']{1,40})'", RegexOptions.Compiled);

    /// <summary>Reads the tier aliases the CLI advertises in its <c>--model</c> help.</summary>
    public static IReadOnlyList<string> AliasesInHelp(string? helpText)
    {
        var aliases = new List<string>();
        if (string.IsNullOrEmpty(helpText)) return aliases;

        string block = ModelBlock(helpText!);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Quoted.Matches(block))
        {
            string token = match.Groups[1].Value.Trim().ToLowerInvariant();
            // Skip full model ids and anything that is clearly not a bare alias.
            if (token.Length == 0 || token.StartsWith("claude", StringComparison.Ordinal)) continue;
            if (token.IndexOf(' ') >= 0) continue;
            if (seen.Add(token)) aliases.Add(token);
        }
        return aliases;
    }

    /// <summary>Compares the CLI's advertised aliases against what the panel offers.</summary>
    public static ModelCatalogDiff Compare(string? helpText, IEnumerable<string>? offered)
    {
        IReadOnlyList<string> cli = AliasesInHelp(helpText);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (offered != null)
        {
            foreach (string o in offered)
            {
                if (!string.IsNullOrEmpty(o)) known.Add(o);
            }
        }

        var newInCli = new List<string>();
        foreach (string alias in cli)
        {
            if (!known.Contains(alias)) newInCli.Add(alias);
        }

        var missing = new List<string>();
        var cliSet = new HashSet<string>(cli, StringComparer.OrdinalIgnoreCase);
        foreach (string k in known)
        {
            if (!cliSet.Contains(k)) missing.Add(k);
        }

        // Only claim staleness when the CLI actually said something; empty help proves nothing.
        return cli.Count == 0
            ? new ModelCatalogDiff(Array.Empty<string>(), Array.Empty<string>())
            : new ModelCatalogDiff(newInCli, missing);
    }

    // The slice of help text describing --model, so quotes from unrelated options are not read as
    // model aliases. Falls back to a window after the marker when the next option cannot be found.
    private static string ModelBlock(string helpText)
    {
        int start = helpText.IndexOf("--model", StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        // The next option starts on a later line with "--"; search past the current line.
        int lineEnd = helpText.IndexOf('\n', start);
        int next = lineEnd < 0 ? -1 : helpText.IndexOf("--", lineEnd, StringComparison.Ordinal);
        int end = next > start ? next : Math.Min(helpText.Length, start + 400);
        return helpText.Substring(start, Math.Max(0, end - start));
    }
}
