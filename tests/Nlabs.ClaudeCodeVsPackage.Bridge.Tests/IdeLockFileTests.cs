using System;
using System.IO;
using Nlabs.ClaudeCodeVsPackage.Bridge.Ide;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests
{
    public class IdeLockFileTests
    {
        [Fact]
        public void Write_creates_a_lock_file_with_the_endpoint_details()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nlabs-ide-" + Guid.NewGuid().ToString("n"));
            try
            {
                var lockFile = new IdeLockFile(dir);

                string path = lockFile.Write(
                    port: 51234,
                    authToken: "tok\"en",                       // contains a quote to force escaping
                    processId: 4242,
                    ideName: "Visual Studio",
                    workspaceFolders: new[] { @"C:\proj\a", @"C:\proj\b" });

                Assert.True(File.Exists(path));
                Assert.EndsWith("51234.lock", path);

                string json = File.ReadAllText(path);
                Assert.Contains("\"pid\":4242", json);
                Assert.Contains("\"transport\":\"ws\"", json);
                Assert.Contains("\"runningInWindows\":true", json);
                Assert.Contains("\"ideName\":\"Visual Studio\"", json);
                Assert.Contains("\"authToken\":\"tok\\\"en\"", json);   // the quote is escaped
                Assert.Contains("C:\\\\proj\\\\a", json);               // backslashes are escaped
                Assert.Contains("\"workspaceFolders\":[", json);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Remove_deletes_the_file_and_is_safe_when_missing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nlabs-ide-" + Guid.NewGuid().ToString("n"));
            try
            {
                var lockFile = new IdeLockFile(dir);

                lockFile.Remove(9999); // nothing there yet - must not throw

                string path = lockFile.Write(9999, "t", 1, "Visual Studio", Array.Empty<string>());
                Assert.True(File.Exists(path));

                lockFile.Remove(9999);
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
