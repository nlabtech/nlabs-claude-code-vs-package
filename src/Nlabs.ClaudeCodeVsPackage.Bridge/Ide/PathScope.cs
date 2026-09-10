using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Ide;

/// <summary>What a path check decided.</summary>
public enum PathVerdict
{
    Allowed,
    /// <summary>Not a usable local path: relative, drive-relative, a device path, or malformed.</summary>
    Invalid,
    /// <summary>Under none of the roots - the open solution, its projects, the panel's chosen folder.</summary>
    OutsideScope,
    /// <summary>A file the permission floor forbids reading, wherever it sits.</summary>
    Secret,
}

/// <summary>
/// Decides whether an IDE tool may open a path.
///
/// The IDE tools do not pass through the approval hook - its matcher covers the CLI's own editing
/// and shell tools, not these - so nothing asks before one of them runs. That matters for a pair:
/// openFile can open any path and select its lines, and getCurrentSelection returns the selected
/// text. Together they read any file on the machine, including the ones the permission floor denies
/// to the CLI's own Read tool - a .env, a private key. A floor is only as strong as the easiest way
/// around it, so every tool that opens a file checks here first.
///
/// Two rules. The path must sit under one of the given roots, and with no roots nothing is allowed:
/// "the workspace is unknown" must never read as "anywhere". And a file named like a secret in the
/// permission floor is refused even inside a root, because inside the workspace is exactly where a
/// .env lives. Those names are read from <see cref="PermissionPolicy"/> itself, so the two lists
/// cannot drift apart.
///
/// Pure and dependency-free, so every rule is unit-tested.
/// </summary>
public static class PathScope
{
    private static readonly Regex[] SecretNames = BuildSecretNames();

    // A full local path starts with a drive and a separator, or is UNC. "C:file" and "\file" are
    // rooted but resolve against whatever the current directory happens to be - not a scope anyone chose.
    private static readonly Regex FullyQualified = new Regex(@"^([A-Za-z]:[\\/]|\\\\[^\\?.])", RegexOptions.Compiled);

    /// <param name="path">The path a tool was asked to open.</param>
    /// <param name="roots">The workspace: the open solution, its projects, the panel's chosen folder.</param>
    /// <param name="refuseSecrets">
    /// False only where a file is shown to the developer rather than returned to the model - a diff of
    /// a .env, say - so the workspace rule still applies but the secret rule does not.
    /// </param>
    public static PathVerdict Check(string? path, IEnumerable<string?>? roots, bool refuseSecrets = true)
    {
        string? full = Normalize(path);
        if (full == null) return PathVerdict.Invalid;
        if (refuseSecrets && IsSecret(full)) return PathVerdict.Secret;

        if (roots != null)
        {
            foreach (string? root in roots)
            {
                if (IsUnder(full, root)) return PathVerdict.Allowed;
            }
        }

        return PathVerdict.OutsideScope;
    }

    /// <summary>Whether a file is named like one the permission floor forbids reading.</summary>
    public static bool IsSecret(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string name;
        try { name = Path.GetFileName(path!.TrimEnd('\\', '/')); }
        catch (ArgumentException) { return false; }

        foreach (Regex rule in SecretNames)
        {
            if (rule.IsMatch(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a fully resolved path is the root or below it. Compared on whole segments, so
    /// C:\work does not cover C:\work-old - which a plain prefix test would allow.
    /// </summary>
    public static bool IsUnder(string fullPath, string? root)
    {
        string? top = Normalize(root);
        if (top == null) return false;

        top = top.TrimEnd('\\');
        return fullPath.Equals(top, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A fully resolved local path, or null when there is none to be had. Resolving comes first
    /// on purpose: C:\work\..\keys\id_rsa begins with C:\work as text but is not under it.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path!.Trim().Trim('"');

        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(p, UriKind.Absolute, out Uri? uri) || !uri.IsFile) return null;
            p = uri.LocalPath;
        }

        // Device and extended-length forms skip the normalisation everything else relies on, and no
        // tool here needs them. The pattern below also refuses anything not fully qualified.
        if (!FullyQualified.IsMatch(p)) return null;

        try
        {
            return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                   ex is PathTooLongException || ex is System.Security.SecurityException)
        {
            return null;
        }
    }

    // Each Read(...) rule in the floor names a file, sometimes behind a path prefix ("./.env",
    // "**/*.pem"). Only the name is kept: to these tools a nested .env is as much a secret as one
    // at the root.
    private static Regex[] BuildSecretNames()
    {
        var rules = new List<Regex>();
        foreach (string rule in PermissionPolicy.DefaultDenyRules())
        {
            if (!rule.StartsWith("Read(", StringComparison.Ordinal) || !rule.EndsWith(")", StringComparison.Ordinal)) continue;

            string glob = rule.Substring(5, rule.Length - 6);
            int slash = glob.LastIndexOf('/');
            if (slash >= 0) glob = glob.Substring(slash + 1);
            if (glob.Length == 0) continue;

            string pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            rules.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        return rules.ToArray();
    }
}
