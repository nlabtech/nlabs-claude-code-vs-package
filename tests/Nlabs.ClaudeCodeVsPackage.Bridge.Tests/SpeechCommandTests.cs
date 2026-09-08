using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class SpeechCommandTests
{
    [Fact]
    public void IsConfigured_only_when_a_command_is_set()
    {
        Assert.False(SpeechCommand.IsConfigured(null));
        Assert.False(SpeechCommand.IsConfigured("   "));
        Assert.True(SpeechCommand.IsConfigured("stt {audio}"));
    }

    [Fact]
    public void Compose_splits_the_executable_from_its_arguments()
    {
        var (file, args) = SpeechCommand.Compose("stt.exe --lang tr {audio}", @"C:\tmp\a.wav");

        Assert.Equal("stt.exe", file);
        Assert.Equal(@"--lang tr C:\tmp\a.wav", args);
    }

    [Fact]
    public void Compose_handles_a_quoted_executable_path()
    {
        var (file, args) = SpeechCommand.Compose("\"C:\\Program Files\\stt\\stt.exe\" -f {audio}", @"C:\tmp\a.wav");

        Assert.Equal(@"C:\Program Files\stt\stt.exe", file);
        Assert.Equal(@"-f C:\tmp\a.wav", args);
    }

    [Fact]
    public void Compose_quotes_a_path_with_spaces()
    {
        var (_, args) = SpeechCommand.Compose("stt {audio}", @"C:\my temp\a.wav");

        Assert.Equal("\"C:\\my temp\\a.wav\"", args);
    }

    [Fact]
    public void Compose_does_not_double_quote_an_already_quoted_placeholder()
    {
        var (_, args) = SpeechCommand.Compose("stt \"{audio}\"", @"C:\my temp\a.wav");

        Assert.Equal("\"C:\\my temp\\a.wav\"", args);
    }

    [Fact]
    public void Compose_of_an_empty_template_yields_nothing()
    {
        var (file, args) = SpeechCommand.Compose("", "a.wav");

        Assert.Equal(string.Empty, file);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void CleanTranscript_joins_the_spoken_lines_and_drops_noise()
    {
        const string output =
            "INFO loading model\n" +
            "[00:00.000 --> 00:02.000]  ignored timing line\n" +
            "Merhaba dunya\n" +
            "\n" +
            "ikinci cumle\n" +
            "42%\n";

        string text = SpeechCommand.CleanTranscript(output);

        Assert.Equal("Merhaba dunya ikinci cumle", text);
    }

    [Fact]
    public void CleanTranscript_tolerates_empty_output()
    {
        Assert.Equal(string.Empty, SpeechCommand.CleanTranscript(null));
        Assert.Equal(string.Empty, SpeechCommand.CleanTranscript("   \n  "));
    }
}
