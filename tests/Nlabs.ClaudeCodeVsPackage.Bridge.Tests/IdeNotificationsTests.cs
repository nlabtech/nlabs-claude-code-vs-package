using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Ide;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests
{
    public class IdeNotificationsTests
    {
        [Fact]
        public void SelectionChanged_has_the_method_zero_based_range_and_no_id()
        {
            string json = IdeNotifications.SelectionChanged(
                @"C:\proj\Program.cs", "var x = 1;",
                startLine: 4, startCharacter: 8,
                endLine: 4, endCharacter: 18,
                isEmpty: false);

            var n = JObject.Parse(json);

            Assert.Equal("2.0", (string?)n["jsonrpc"]);
            Assert.Equal("selection_changed", (string?)n["method"]);
            Assert.Null(n["id"]); // a notification never carries an id

            var p = n["params"]!;
            Assert.Equal("var x = 1;", (string?)p["text"]);
            Assert.Equal(@"C:\proj\Program.cs", (string?)p["filePath"]);
            Assert.StartsWith("file:///", (string?)p["fileUrl"]);

            var sel = p["selection"]!;
            Assert.Equal(4, (int)sel["start"]!["line"]!);
            Assert.Equal(8, (int)sel["start"]!["character"]!);
            Assert.Equal(18, (int)sel["end"]!["character"]!);
            Assert.False((bool)sel["isEmpty"]!);
        }

        [Fact]
        public void SelectionChanged_marks_an_empty_selection()
        {
            string json = IdeNotifications.SelectionChanged(
                @"C:\a.cs", "", 0, 0, 0, 0, isEmpty: true);

            var sel = JObject.Parse(json)["params"]!["selection"]!;
            Assert.True((bool)sel["isEmpty"]!);
            Assert.Equal("", (string?)JObject.Parse(json)["params"]!["text"]);
        }

        [Fact]
        public void AtMentioned_carries_the_file_and_zero_based_line_range()
        {
            string json = IdeNotifications.AtMentioned(@"C:\proj\Service.cs", 9, 15);

            var n = JObject.Parse(json);
            Assert.Equal("at_mentioned", (string?)n["method"]);
            Assert.Null(n["id"]);

            var p = n["params"]!;
            Assert.Equal(@"C:\proj\Service.cs", (string?)p["filePath"]);
            Assert.Equal(9, (int)p["lineStart"]!);
            Assert.Equal(15, (int)p["lineEnd"]!);
        }
    }
}
