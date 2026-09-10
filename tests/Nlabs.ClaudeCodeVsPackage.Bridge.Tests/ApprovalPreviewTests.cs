using System.Linq;
using System.Text;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class ApprovalPreviewTests
{
    [Fact]
    public void A_shell_call_shows_the_command_not_the_json()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "Bash", "{\"command\":\"dotnet build\",\"description\":\"Build the solution\"}");

        Assert.Equal(PreviewKind.Command, p.Kind);
        Assert.Equal("dotnet build", p.Text);
        Assert.Equal("Build the solution", p.Note);
        Assert.Equal("wantsToRun", p.TitleKey);
    }

    [Fact]
    public void A_command_without_a_description_carries_no_note()
    {
        ApprovalPreview p = ApprovalPreview.Build("Bash", "{\"command\":\"git status\"}");

        Assert.Equal("git status", p.Text);
        Assert.Null(p.Note);
    }

    [Fact]
    public void An_edit_becomes_a_diff_of_the_two_fragments()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "Edit",
            "{\"file_path\":\"C:\\\\src\\\\a.cs\",\"old_string\":\"int a = 1;\",\"new_string\":\"int a = 2;\"}");

        Assert.Equal(PreviewKind.Diff, p.Kind);
        Assert.Equal("wantsToEdit", p.TitleKey);
        Assert.Equal("C:\\src\\a.cs", p.Target);
        Assert.Equal(1, p.Added);
        Assert.Equal(1, p.Removed);
        Assert.Contains(p.Lines, l => l.Kind == DiffKind.Removed && l.Text == "int a = 1;");
        Assert.Contains(p.Lines, l => l.Kind == DiffKind.Added && l.Text == "int a = 2;");
    }

    [Fact]
    public void Unchanged_lines_around_a_change_are_kept_as_context()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "Edit",
            "{\"old_string\":\"one\\ntwo\\nthree\",\"new_string\":\"one\\nTWO\\nthree\"}");

        Assert.Equal(1, p.Added);
        Assert.Equal(1, p.Removed);
        Assert.Equal(2, p.Lines.Count(l => l.Kind == DiffKind.Context));
    }

    [Fact]
    public void A_long_run_of_unchanged_lines_is_folded_away()
    {
        var body = new StringBuilder();
        for (int i = 0; i < 40; i++) body.Append("line ").Append(i).Append("\\n");

        string old = "first\\n" + body + "last";
        string @new = "FIRST\\n" + body + "last";

        ApprovalPreview p = ApprovalPreview.Build(
            "Edit", "{\"old_string\":\"" + old + "\",\"new_string\":\"" + @new + "\"}");

        DiffLine gap = Assert.Single(p.Lines, l => l.Kind == DiffKind.Gap);
        Assert.Equal("38", gap.Text); // 40 body lines + "last", less the 3 kept as context
        Assert.DoesNotContain(p.Lines, l => l.Text == "line 20");
    }

    [Fact]
    public void A_single_hidden_line_is_shown_rather_than_folded()
    {
        // Two changes seven lines apart: three of context either side leaves exactly one in between,
        // and "1 line hidden" would take more room than the line itself.
        ApprovalPreview p = ApprovalPreview.Build(
            "Edit",
            "{\"old_string\":\"A\\n1\\n2\\n3\\n4\\n5\\n6\\n7\\nB\",\"new_string\":\"a\\n1\\n2\\n3\\n4\\n5\\n6\\n7\\nb\"}");

        Assert.DoesNotContain(p.Lines, l => l.Kind == DiffKind.Gap);
        Assert.Contains(p.Lines, l => l.Text == "4");
    }

    [Fact]
    public void An_edit_that_only_adds_text_is_shown_as_content()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "Edit", "{\"file_path\":\"a.cs\",\"old_string\":\"\",\"new_string\":\"a\\nb\"}");

        Assert.Equal(PreviewKind.Content, p.Kind);
        Assert.Equal("a\nb", p.Text);
        Assert.Equal(2, p.Added);
    }

    [Fact]
    public void A_write_shows_the_content_and_its_size()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "Write", "{\"file_path\":\"new.cs\",\"content\":\"one\\ntwo\\nthree\"}");

        Assert.Equal(PreviewKind.Content, p.Kind);
        Assert.Equal("wantsToCreate", p.TitleKey);
        Assert.Equal("new.cs", p.Target);
        Assert.Equal(3, p.Added);
    }

    [Fact]
    public void A_multi_edit_diffs_every_hunk()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "MultiEdit",
            "{\"file_path\":\"a.cs\",\"edits\":[" +
            "{\"old_string\":\"one\",\"new_string\":\"ONE\"}," +
            "{\"old_string\":\"two\",\"new_string\":\"TWO\"}]}");

        Assert.Equal(PreviewKind.Diff, p.Kind);
        Assert.Equal(2, p.Added);
        Assert.Equal(2, p.Removed);
        Assert.Contains(p.Lines, l => l.Text == "ONE");
        Assert.Contains(p.Lines, l => l.Text == "TWO");
    }

    [Fact]
    public void A_notebook_edit_shows_the_replacement_cell()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "NotebookEdit", "{\"notebook_path\":\"n.ipynb\",\"new_source\":\"print(1)\"}");

        Assert.Equal(PreviewKind.Content, p.Kind);
        Assert.Equal("n.ipynb", p.Target);
        Assert.Equal("print(1)", p.Text);
    }

    [Fact]
    public void A_read_names_the_file_and_nothing_else()
    {
        ApprovalPreview p = ApprovalPreview.Build("Read", "{\"file_path\":\"a.cs\"}");

        Assert.Equal(PreviewKind.Target, p.Kind);
        Assert.Equal("wantsToRead", p.TitleKey);
        Assert.Equal("a.cs", p.Target);
    }

    [Fact]
    public void A_search_shows_its_pattern()
    {
        ApprovalPreview p = ApprovalPreview.Build("Grep", "{\"pattern\":\"TODO\",\"path\":\"src\"}");

        Assert.Equal(PreviewKind.Target, p.Kind);
        Assert.Equal("wantsToSearch", p.TitleKey);
        Assert.Equal("TODO", p.Text);
        Assert.Equal("src", p.Target);
    }

    [Fact]
    public void Opening_a_solution_names_the_solution()
    {
        ApprovalPreview p = ApprovalPreview.Build("openSolution", "{\"path\":\"C:\\\\work\\\\App.slnx\"}");

        Assert.Equal(PreviewKind.Target, p.Kind);
        Assert.Equal("wantsToOpen", p.TitleKey);
        Assert.Equal("C:\\work\\App.slnx", p.Target);
    }

    [Fact]
    public void A_fetch_shows_the_url()
    {
        ApprovalPreview p = ApprovalPreview.Build("WebFetch", "{\"url\":\"https://example.com\"}");

        Assert.Equal("wantsToFetch", p.TitleKey);
        Assert.Equal("https://example.com", p.Text);
    }

    [Theory]
    [InlineData("SomeFutureTool", "{\"a\":1}")]
    [InlineData("Bash", "{\"description\":\"no command here\"}")]
    [InlineData("Edit", "{\"file_path\":\"a.cs\"}")]
    [InlineData("MultiEdit", "{\"file_path\":\"a.cs\",\"edits\":[]}")]
    public void Anything_we_have_no_shape_for_falls_back_to_the_raw_json(string tool, string json)
    {
        ApprovalPreview p = ApprovalPreview.Build(tool, json);

        Assert.Equal(PreviewKind.Raw, p.Kind);
        Assert.Equal(json, p.Text);
    }

    [Fact]
    public void Malformed_json_never_throws()
    {
        ApprovalPreview p = ApprovalPreview.Build("Edit", "{not json");

        Assert.Equal(PreviewKind.Raw, p.Kind);
        Assert.Equal("{not json", p.Text);
    }

    [Fact]
    public void A_huge_edit_skips_the_diff_table_but_still_shows_both_sides()
    {
        var old = new StringBuilder();
        var @new = new StringBuilder();
        for (int i = 0; i < 600; i++)
        {
            old.Append("old ").Append(i).Append("\\n");
            @new.Append("new ").Append(i).Append("\\n");
        }

        ApprovalPreview p = ApprovalPreview.Build(
            "Edit", "{\"old_string\":\"" + old + "x\",\"new_string\":\"" + @new + "x\"}");

        Assert.Equal(PreviewKind.Diff, p.Kind);
        Assert.True(p.Removed > 500);
        Assert.True(p.Added > 500);
    }

    [Fact]
    public void Windows_line_endings_do_not_show_up_as_changes()
    {
        ApprovalPreview p = ApprovalPreview.Build(
            "Edit", "{\"old_string\":\"a\\r\\nb\",\"new_string\":\"a\\nb\"}");

        Assert.Equal(0, p.Added);
        Assert.Equal(0, p.Removed);
    }
}
