using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Ide
{
    /// <summary>
    /// The discovery file that lets Claude Code find this IDE.
    ///
    /// Claude Code discovers a running IDE by scanning <c>~/.claude/ide</c> for a
    /// <c>&lt;port&gt;.lock</c> file describing a local endpoint; the CLI's <c>/ide</c> command
    /// connects to that port. Writing this file is what makes the extension appear as a
    /// first-class IDE ("Connected to Visual Studio") instead of a bolted-on tool server -
    /// no <c>claude mcp add</c>, and the tools are not namespaced behind an <c>mcp__</c> prefix.
    ///
    /// The directory is injectable so the behaviour can be unit-tested without touching the
    /// real home folder. The JSON is written by hand to keep this core free of dependencies.
    /// </summary>
    public sealed class IdeLockFile
    {
        private readonly string _directory;

        public IdeLockFile(string? directory = null)
        {
            _directory = directory ?? DefaultDirectory();
        }

        public static string DefaultDirectory()
        {
            string home =
                Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".claude", "ide");
        }

        public string PathFor(int port) => Path.Combine(_directory, port + ".lock");

        /// <summary>Writes (or overwrites) the lock file for this endpoint and returns its path.</summary>
        public string Write(int port, string authToken, int processId, string ideName, IEnumerable<string> workspaceFolders)
        {
            Directory.CreateDirectory(_directory);

            var json = new StringBuilder();
            json.Append('{');
            json.Append("\"pid\":").Append(processId).Append(',');
            json.Append("\"ideName\":").Append(Quote(ideName)).Append(',');
            json.Append("\"transport\":\"ws\",");
            json.Append("\"runningInWindows\":true,");
            json.Append("\"authToken\":").Append(Quote(authToken)).Append(',');
            json.Append("\"workspaceFolders\":[");
            bool first = true;
            foreach (var folder in workspaceFolders)
            {
                if (!first) json.Append(',');
                json.Append(Quote(folder));
                first = false;
            }
            json.Append("]}");

            string path = PathFor(port);
            File.WriteAllText(path, json.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        /// <summary>Removes the lock file for a port; safe to call when it does not exist.</summary>
        public void Remove(int port)
        {
            try
            {
                string path = PathFor(port);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // best effort; a stale lock is cleaned up on the next scan
            }
        }

        private static string Quote(string? value)
        {
            if (value == null) return "\"\"";

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
