using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>One stored message: who said it and the text.</summary>
public sealed class MessageRecord
{
    public bool IsUser { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>One stored chat: its title, the CLI session id used to resume it, and its messages.</summary>
public sealed class ConversationRecord
{
    public string Title { get; set; } = "New chat";
    public string? CliSessionId { get; set; }
    public List<MessageRecord> Messages { get; set; } = new List<MessageRecord>();
}

/// <summary>
/// Persists the panel's chats so they survive closing Visual Studio. One JSON file per workspace
/// under the user's local app data; the CLI session id is kept too, so a restored chat resumes.
/// All disk access is best-effort - a failure just means an empty history, never a crash.
/// </summary>
public sealed class ConversationStore
{
    private readonly string _dir;

    public ConversationStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nLabtech", "ClaudeCodePanel");
    }

    public List<ConversationRecord> Load(string workspaceKey)
    {
        try
        {
            string path = FileFor(workspaceKey);
            if (!File.Exists(path)) return new List<ConversationRecord>();
            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<ConversationRecord>>(json) ?? new List<ConversationRecord>();
        }
        catch
        {
            return new List<ConversationRecord>();
        }
    }

    public void Save(string workspaceKey, IEnumerable<ConversationRecord> conversations)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string json = JsonConvert.SerializeObject(conversations, Formatting.Indented);
            File.WriteAllText(FileFor(workspaceKey), json);
        }
        catch
        {
            // best-effort; a persistence failure must never break the panel
        }
    }

    private string FileFor(string workspaceKey)
    {
        using (var sha = SHA1.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(workspaceKey ?? string.Empty));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return Path.Combine(_dir, sb.ToString() + ".json");
        }
    }
}
