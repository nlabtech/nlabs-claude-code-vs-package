using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class PermissionPolicyTests
{
    [Fact]
    public void BuildSettingsJson_denies_destructive_and_secret_operations()
    {
        var settings = JObject.Parse(PermissionPolicy.BuildSettingsJson());
        var rules = ((JArray)settings["permissions"]!["deny"]!).ToObject<string[]>()!;

        Assert.Contains("Bash(rm:*)", rules);
        Assert.Contains("Bash(sudo:*)", rules);
        Assert.Contains("Bash(git push --force:*)", rules);
        Assert.Contains("Read(./.env)", rules);
    }

    [Fact]
    public void The_floor_names_the_key_and_credential_files_it_refuses()
    {
        // PathScope builds its secret list from these same rules, so a gap here is a gap in both
        // the CLI's own Read and in every IDE tool that opens a file.
        var settings = JObject.Parse(PermissionPolicy.BuildSettingsJson());
        var rules = ((JArray)settings["permissions"]!["deny"]!).ToObject<string[]>()!;

        Assert.Contains("Read(**/id_ed25519)", rules);
        Assert.Contains("Read(**/*.pfx)", rules);
        Assert.Contains("Read(**/*.key)", rules);
        Assert.Contains("Read(**/credentials)", rules);
        Assert.Contains("Read(**/secrets.json)", rules);
    }

    [Fact]
    public void BuildSettingsJson_appends_extra_deny_rules_on_top_of_the_floor()
    {
        var settings = JObject.Parse(PermissionPolicy.BuildSettingsJson(new[] { "Bash(docker:*)" }));
        var rules = ((JArray)settings["permissions"]!["deny"]!).ToObject<string[]>()!;

        Assert.Contains("Bash(rm:*)", rules);   // floor still present
        Assert.Contains("Bash(docker:*)", rules); // extra rule added
    }
}
