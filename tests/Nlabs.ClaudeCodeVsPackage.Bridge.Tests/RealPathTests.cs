using Nlabs.ClaudeCodeVsPackage.Bridge.Ide;
using System;
using System.IO;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

/// <summary>
/// These touch the disk on purpose. <see cref="PathScopeTests"/> covers the rules with a stub
/// resolver; what is left to get wrong here is the call into Windows itself, and a resolver that
/// silently answers null for everything would pass every rule test while protecting nothing.
/// </summary>
public class RealPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nlabs-realpath-" + Guid.NewGuid().ToString("n"));

    public RealPathTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_file_that_exists_resolves_to_a_plain_local_path()
    {
        string file = Path.Combine(_root, "a-long-enough-name-to-avoid-short-forms.cs");
        File.WriteAllText(file, "x");

        string? real = RealPath.Resolve(file);

        Assert.NotNull(real);
        Assert.DoesNotContain("?", real!);                       // the extended-length prefix is gone
        Assert.Equal(PathScope.Normalize(real), real);           // and what comes back is a path PathScope accepts
        Assert.EndsWith(Path.GetFileName(file), real!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_directory_resolves_too()
    {
        // Directories need FILE_FLAG_BACKUP_SEMANTICS; without it this is the case that returns null,
        // and a junction is a directory - so getting this wrong would disable the whole check.
        string? real = RealPath.Resolve(_root);

        Assert.NotNull(real);
        Assert.EndsWith(Path.GetFileName(_root), real!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_that_does_not_exist_yet_is_reached_through_its_nearest_parent()
    {
        // openDiff proposes files that are not there yet. The link worth catching is a directory
        // above them, so the walk up has to keep going until something can be opened.
        string missing = Path.Combine(_root, "not-created", "either", "New.cs");

        string? real = RealPath.Resolve(missing);

        Assert.NotNull(real);
        Assert.EndsWith(Path.Combine("not-created", "either", "New.cs"), real!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_usable_comes_back_as_nothing()
    {
        Assert.Null(RealPath.Resolve(null));
        Assert.Null(RealPath.Resolve("   "));
    }

    [Fact]
    public void A_real_junction_out_of_the_workspace_is_refused()
    {
        // The whole point, end to end and on a real disk: a junction is a directory any user can
        // create, and until it is followed it reads as an ordinary folder inside the workspace.
        string workspace = Path.Combine(_root, "workspace");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secrets.txt"), "x");

        string junction = Path.Combine(workspace, "vendor");
        if (!MakeJunction(junction, outside)) return;   // no junction, nothing to prove

        string through = Path.Combine(junction, "secrets.txt");
        var roots = new[] { workspace };

        Assert.Equal(PathVerdict.Allowed, PathScope.Check(through, roots));
        Assert.Equal(PathVerdict.OutsideScope, PathScope.Check(through, roots, resolve: RealPath.Resolve));
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
