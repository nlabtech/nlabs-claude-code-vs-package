using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Nlabs.ClaudeCodeVsPackage.Bridge.Ide;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class PathScopeTests
{
    private static readonly string[] Roots = { @"C:\work\app" };

    [Theory]
    [InlineData(@"C:\work\app\Program.cs")]
    [InlineData(@"C:\work\app\src\deep\Thing.cs")]
    [InlineData(@"c:\WORK\App\program.cs")]
    [InlineData("C:/work/app/src/Thing.cs")]
    [InlineData("file:///C:/work/app/Program.cs")]
    public void A_path_under_a_root_is_allowed(string path)
    {
        Assert.Equal(PathVerdict.Allowed, PathScope.Check(path, Roots));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\notes.txt")]
    [InlineData(@"C:\work\app-old\Program.cs")]       // shares the text prefix, not the folder
    [InlineData(@"C:\work\app\..\other\Program.cs")]  // resolves to somewhere else
    [InlineData(@"\\server\share\Program.cs")]
    public void A_path_outside_every_root_is_refused(string path)
    {
        Assert.Equal(PathVerdict.OutsideScope, PathScope.Check(path, Roots));
    }

    [Fact]
    public void With_no_roots_nothing_is_allowed()
    {
        // "The workspace is unknown" must never mean "anywhere".
        Assert.Equal(PathVerdict.OutsideScope, PathScope.Check(@"C:\work\app\Program.cs", new string[0]));
        Assert.Equal(PathVerdict.OutsideScope, PathScope.Check(@"C:\work\app\Program.cs", null));
    }

    [Fact]
    public void A_blank_root_is_skipped_rather_than_covering_everything()
    {
        Assert.Equal(PathVerdict.OutsideScope,
            PathScope.Check(@"C:\work\app\Program.cs", new string?[] { null, "", "   " }));
    }

    [Fact]
    public void A_trailing_separator_on_the_root_changes_nothing()
    {
        Assert.Equal(PathVerdict.Allowed, PathScope.Check(@"C:\work\app\a.cs", new[] { @"C:\work\app\" }));
    }

    [Theory]
    [InlineData(@"C:\work\app\.env")]
    [InlineData(@"C:\work\app\.env.local")]
    [InlineData(@"C:\work\app\config\.env")]
    [InlineData(@"C:\work\app\certs\server.pem")]
    [InlineData(@"C:\work\app\keys\id_rsa")]
    [InlineData(@"C:\work\app\keys\id_ed25519")]
    [InlineData(@"C:\work\app\certs\site.pfx")]
    [InlineData(@"C:\work\app\certs\site.key")]
    [InlineData(@"C:\work\app\.npmrc")]
    [InlineData(@"C:\work\app\secrets.json")]
    [InlineData(@"C:\work\app\.aws\credentials")]
    public void A_secret_is_refused_even_inside_a_root(string path)
    {
        Assert.Equal(PathVerdict.Secret, PathScope.Check(path, Roots));
    }

    [Theory]
    [InlineData(@"C:\work\app\env.cs")]
    [InlineData(@"C:\work\app\Environment.cs")]
    [InlineData(@"C:\work\app\keys\id_rsa.pub")]
    [InlineData(@"C:\work\app\keys\id_ed25519.pub")]
    [InlineData(@"C:\work\app\Credentials.cs")]
    [InlineData(@"C:\work\app\appsettings.json")]
    public void A_name_that_only_resembles_a_secret_is_allowed(string path)
    {
        Assert.Equal(PathVerdict.Allowed, PathScope.Check(path, Roots));
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData(@"src\Program.cs")]
    [InlineData(@"C:Program.cs")]
    [InlineData(@"\Program.cs")]
    [InlineData(@"\\?\C:\work\app\Program.cs")]
    [InlineData(@"\\.\C:\work\app\Program.cs")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_a_full_local_path_is_invalid(string? path)
    {
        Assert.Equal(PathVerdict.Invalid, PathScope.Check(path, Roots));
    }

    [Fact]
    public void A_secret_shown_only_to_the_developer_is_held_to_the_workspace_alone()
    {
        // A diff puts the file in front of the developer, not the model: the workspace rule still holds.
        Assert.Equal(PathVerdict.Allowed, PathScope.Check(@"C:\work\app\.env", Roots, refuseSecrets: false));
        Assert.Equal(PathVerdict.OutsideScope, PathScope.Check(@"C:\elsewhere\.env", Roots, refuseSecrets: false));
    }

    [Fact]
    public void Every_file_the_permission_floor_denies_is_a_secret_here_too()
    {
        // The two lists must not drift: a Read rule added to the floor has to close the IDE route too.
        int checkedRules = 0;
        foreach (string rule in PermissionPolicy.DefaultDenyRules())
        {
            if (!rule.StartsWith("Read(")) continue;

            string glob = rule.Substring(5, rule.Length - 6);
            string name = glob.Substring(glob.LastIndexOf('/') + 1).Replace("*", "sample");
            Assert.True(PathScope.IsSecret(@"C:\work\app\" + name), rule);
            checkedRules++;
        }

        Assert.True(checkedRules > 0);
    }

    [Fact]
    public void A_junction_out_of_the_workspace_is_refused_for_where_it_really_goes()
    {
        // C:\work\app\vendor is a junction to C:\Users\me\.ssh. As text it is inside the workspace,
        // and anyone can create one without admin rights.
        Assert.Equal(
            PathVerdict.OutsideScope,
            PathScope.Check(@"C:\work\app\vendor\config", Roots, resolve: _ => @"C:\Users\me\.ssh\config"));
    }

    [Fact]
    public void A_link_wearing_an_innocent_name_is_refused_for_the_name_it_really_has()
    {
        // The secret list works on names, so a link is how a secret gets a name that is not on it.
        Assert.Equal(
            PathVerdict.Secret,
            PathScope.Check(@"C:\work\app\notes.txt", Roots, resolve: _ => @"C:\work\app\.env"));
    }

    [Fact]
    public void A_link_that_stays_inside_the_workspace_is_still_allowed()
    {
        Assert.Equal(
            PathVerdict.Allowed,
            PathScope.Check(@"C:\work\app\link\a.cs", Roots, resolve: _ => @"C:\work\app\real\a.cs"));
    }

    [Fact]
    public void A_path_that_cannot_be_resolved_keeps_the_verdict_it_had()
    {
        // Resolving is best-effort: a file that does not exist yet, a disk that went away. It may
        // take an answer away, never hand one out, so an unresolvable path is judged on its text.
        Assert.Equal(PathVerdict.Allowed, PathScope.Check(@"C:\work\app\new.cs", Roots, resolve: _ => null));
        Assert.Equal(PathVerdict.OutsideScope, PathScope.Check(@"C:\elsewhere\x.cs", Roots, resolve: _ => null));
    }

    [Fact]
    public void A_resolver_answering_nonsense_does_not_open_a_door()
    {
        Assert.Equal(PathVerdict.Invalid, PathScope.Check(@"C:\work\app\a.cs", Roots, resolve: _ => "not a path"));
    }

    [Fact]
    public void The_resolver_is_not_asked_about_a_path_that_was_already_refused()
    {
        bool asked = false;
        PathScope.Check(@"C:\elsewhere\x.cs", Roots, resolve: p => { asked = true; return p; });

        Assert.False(asked);
    }

    [Fact]
    public void The_extended_length_prefix_is_stripped_off_a_resolved_path()
    {
        // GetFinalPathNameByHandle always answers in that form, and PathScope refuses it outright -
        // so a resolved path that kept the prefix would read as invalid instead of being compared.
        Assert.Equal(@"C:\work\app\a.cs", RealPath.Strip(@"\\?\C:\work\app\a.cs"));
        Assert.Equal(@"\\server\share\a.cs", RealPath.Strip(@"\\?\UNC\server\share\a.cs"));
        Assert.Equal(@"C:\plain\a.cs", RealPath.Strip(@"C:\plain\a.cs"));
    }
}
