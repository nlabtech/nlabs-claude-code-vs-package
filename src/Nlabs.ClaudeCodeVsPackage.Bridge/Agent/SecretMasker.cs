using System;
using System.Text.RegularExpressions;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>
/// Redacts high-confidence secrets from text shown in the panel, so a token or key that Claude
/// echoes back never sits in plain sight - it matters most for build-in-public screenshots, where
/// a leaked credential is permanent.
///
/// The point is safety without wrecking legitimate content, so only patterns that are almost
/// certainly credentials are matched, and each keeps a short recognisable prefix (<c>sk-ant-***</c>,
/// <c>AKIA***</c>) so the reader still sees what kind of secret it was. It is pure and dependency-free,
/// so the rules are unit-tested; the panel calls it at the display boundary, never on what is sent.
/// </summary>
public static class SecretMasker
{
    private const string Mask = "***";

    // Each rule: a pattern, and what to leave in its place. Ordered most-specific first so a longer
    // credential (sk-ant-...) is not half-eaten by a shorter rule (sk-...).
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        // PEM private key blocks - redact the whole body, keep the header so it still reads as a key.
        (new Regex(@"-----BEGIN (?<k>[A-Z ]*?)PRIVATE KEY-----[\s\S]*?-----END \k<k>PRIVATE KEY-----",
            RegexOptions.Compiled), "-----BEGIN ${k}PRIVATE KEY----- " + Mask + " -----END ${k}PRIVATE KEY-----"),

        // JSON Web Tokens (header.payload.signature).
        (new Regex(@"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}", RegexOptions.Compiled), "eyJ" + Mask),

        // Anthropic / OpenAI style keys.
        (new Regex(@"\bsk-ant-[A-Za-z0-9_-]{16,}", RegexOptions.Compiled), "sk-ant-" + Mask),
        (new Regex(@"\bsk-[A-Za-z0-9]{20,}", RegexOptions.Compiled), "sk-" + Mask),

        // GitHub tokens (PAT, OAuth, app, refresh) and the newer fine-grained prefix.
        (new Regex(@"\bghp_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "ghp_" + Mask),
        (new Regex(@"\bgh[ousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "gh_" + Mask),
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled), "github_pat_" + Mask),

        // Cloud providers.
        (new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled), "AKIA" + Mask),
        (new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.Compiled), "AIza" + Mask),

        // Slack tokens.
        (new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled), "xox-" + Mask),

        // Authorization: Bearer <token>.
        (new Regex(@"\b(?<b>[Bb]earer)\s+[A-Za-z0-9._-]{20,}", RegexOptions.Compiled), "${b} " + Mask),

        // password= / pwd= in a connection string or URL query (value up to the next delimiter).
        (new Regex(@"(?<k>(?i:password|pwd))\s*=\s*[^;&""'\s]+", RegexOptions.Compiled), "${k}=" + Mask),
    };

    /// <summary>Returns <paramref name="text"/> with any recognised secrets replaced by a masked hint.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        string result = text!;
        foreach ((Regex pattern, string replacement) in Rules)
        {
            result = pattern.Replace(result, replacement);
        }
        return result;
    }
}
