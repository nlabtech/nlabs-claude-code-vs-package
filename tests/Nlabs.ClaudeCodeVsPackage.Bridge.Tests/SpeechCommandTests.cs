using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class SpeechCommandTests
{
    [Fact]
    public void The_shipped_script_speaks_the_serve_protocol()
    {
        Assert.True(SpeechCommand.SupportsServe(SpeechCommand.LocalScript));
        Assert.Contains(SpeechCommand.EndMarker, SpeechCommand.LocalScript);
        Assert.Contains(SpeechCommand.ErrorMarker, SpeechCommand.LocalScript);
        Assert.Contains("--serve", SpeechCommand.LocalScript);
    }

    [Fact]
    public void A_script_without_the_protocol_is_not_used_as_a_worker()
    {
        Assert.False(SpeechCommand.SupportsServe("print('hello')"));
        Assert.False(SpeechCommand.SupportsServe(null));
    }

    [Fact]
    public void The_superseded_script_is_recognised_so_it_can_be_replaced()
    {
        // The panel only overwrites a script that still opens exactly like the one we used to write.
        const string legacy = SpeechCommand.LegacyScriptOpening + "\n\nimport sys\n";

        Assert.StartsWith(SpeechCommand.LegacyScriptOpening, legacy);
        Assert.False(SpeechCommand.SupportsServe(legacy));
        Assert.False(SpeechCommand.LocalScript.StartsWith(SpeechCommand.LegacyScriptOpening));
    }

    [Fact]
    public void Serve_arguments_quote_the_script_path()
    {
        Assert.Equal("\"C:\\a b\\transcribe.py\" --serve", SpeechCommand.ComposeServe("C:\\a b\\transcribe.py"));
    }

    [Fact]
    public void Downloading_a_model_is_off_unless_it_is_asked_for()
    {
        Assert.DoesNotContain(SpeechCommand.AllowDownloadFlag, SpeechCommand.ComposeServe("s.py"));
        Assert.EndsWith(SpeechCommand.AllowDownloadFlag, SpeechCommand.ComposeServe("s.py", allowDownload: true));
    }

    [Fact]
    public void The_script_says_what_is_missing_rather_than_fetching_it()
    {
        // Without permission the engine reports the model it lacks and stops; the panel asks first.
        Assert.Contains("local_files_only=not allow_download", SpeechCommand.LocalScript);
        Assert.Contains("say(NEED", SpeechCommand.LocalScript);
    }

    [Fact]
    public void The_script_prefers_the_gpu_and_still_runs_without_one()
    {
        // Transcribing on the CPU when a GPU is present was the whole of the wait people noticed.
        Assert.Contains("get_cuda_device_count()", SpeechCommand.LocalScript);
        Assert.Contains("\"cuda\", \"float16\"", SpeechCommand.LocalScript);
        Assert.Contains("\"cpu\", \"int8\"", SpeechCommand.LocalScript);
    }

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
    public void ComposeDefault_survives_a_round_trip_through_Compose()
    {
        string template = SpeechCommand.ComposeDefault(@"C:\Program Files\Python\python.exe", @"C:\tools\transcribe.py");
        var (file, args) = SpeechCommand.Compose(template, @"C:\my temp\a.wav");

        Assert.Equal(@"C:\Program Files\Python\python.exe", file);
        Assert.Equal("\"C:\\tools\\transcribe.py\" \"C:\\my temp\\a.wav\"", args);
    }

    [Fact]
    public void LocalScript_is_plain_ascii_and_transcribes_its_first_argument()
    {
        Assert.All(SpeechCommand.LocalScript, ch => Assert.True(ch < 128, "non-ASCII character in the script"));
        Assert.Contains("faster_whisper", SpeechCommand.LocalScript);

        // The flags are stripped before the positional arguments are read, so the audio path is
        // args[0] whether or not --allow-download was passed.
        Assert.Contains("text_of(model, args[0])", SpeechCommand.LocalScript);
    }

    [Fact]
    public void CleanTranscript_tolerates_empty_output()
    {
        Assert.Equal(string.Empty, SpeechCommand.CleanTranscript(null));
        Assert.Equal(string.Empty, SpeechCommand.CleanTranscript("   \n  "));
    }
}
