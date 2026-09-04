using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>
/// The safety floor for the agentic panel. Whatever permission mode the developer picks - even
/// "accept edits" - a handful of operations must never run automatically: destructive shell
/// commands and reads of local secrets. This builds the <c>permissions.deny</c> settings that
/// <c>claude -p</c> enforces, so the floor is applied by the CLI itself, not by trusting the model.
///
/// The rules use Claude Code's settings matcher form (<c>Tool(pattern)</c>). This core is free of
/// any process or Visual Studio dependency, so the generated settings are unit-tested; the panel
/// writes them to a temp file and passes <c>--settings</c>.
/// </summary>
public static class PermissionPolicy
{
    /// <summary>Operations denied regardless of mode - the always-on safety floor.</summary>
    public static IReadOnlyList<string> DefaultDenyRules() => new[]
    {
        "Bash(rm:*)",
        "Bash(rmdir:*)",
        "Bash(sudo:*)",
        "Bash(git push --force:*)",
        "Bash(git push -f:*)",
        "Bash(git reset --hard:*)",
        "Bash(:(){ :|:& };:)",
        "Read(./.env)",
        "Read(./.env.*)",
        "Read(**/*.pem)",
        "Read(**/id_rsa)",
    };

    /// <summary>
    /// Builds the settings JSON (a <c>permissions.deny</c> list) for <c>claude -p --settings</c>,
    /// optionally adding extra deny rules on top of the default floor.
    /// </summary>
    public static string BuildSettingsJson(IEnumerable<string>? extraDeny = null)
    {
        var deny = new JArray();
        foreach (string rule in DefaultDenyRules())
        {
            deny.Add(rule);
        }
        if (extraDeny != null)
        {
            foreach (string rule in extraDeny)
            {
                deny.Add(rule);
            }
        }

        var settings = new JObject
        {
            ["permissions"] = new JObject { ["deny"] = deny },
        };
        return settings.ToString(Formatting.None);
    }
}
