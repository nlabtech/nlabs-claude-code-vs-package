using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Ide;

/// <summary>
/// Where a path really lands once the file system has had its say.
///
/// <see cref="PathScope"/> decides on text, and text is not the whole truth on Windows: a junction
/// is a directory anyone can create without admin rights, so <c>C:\work\notes</c> can be
/// <c>C:\Users\me\.ssh</c> and read as inside the workspace either way. A symbolic link does the
/// same for a single file, which also means a secret can wear a name the secret list does not know.
///
/// Resolving is best-effort by design. A path that cannot be opened - it does not exist yet, the
/// disk is gone, a driver says no - comes back unresolved, and the caller keeps the verdict it
/// already had. This only ever finds a reason to refuse; it never supplies one to allow.
/// </summary>
public static class RealPath
{
    private const int FileShareAll = 0x1 | 0x2 | 0x4;          // read | write | delete
    private const int OpenExisting = 3;
    private const int BackupSemantics = 0x02000000;            // needed to open a directory handle
    private const int VolumeNameDos = 0x0;
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    /// <summary>
    /// The path with every junction and link along it followed, or null when it cannot be resolved.
    /// A file that does not exist yet is resolved through its nearest existing parent, because the
    /// link that matters is usually a directory somewhere above it.
    /// </summary>
    public static string? Resolve(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        string? direct = Final(fullPath!);
        if (direct != null) return direct;

        // Walk up to something that exists, resolve that, and put the rest back on.
        string tail = string.Empty;
        string? current = fullPath;
        for (int depth = 0; depth < 64; depth++)
        {
            string? parent;
            string name;
            try
            {
                parent = Path.GetDirectoryName(current);
                name = Path.GetFileName(current!);
            }
            catch (ArgumentException) { return null; }

            if (string.IsNullOrEmpty(parent) || name.Length == 0) return null;

            tail = tail.Length == 0 ? name : name + "\\" + tail;
            string? resolved = Final(parent!);
            if (resolved != null) return Path.Combine(resolved, tail);

            current = parent;
        }
        return null;
    }

    // Asks Windows what the handle ended up pointing at. No access is requested, only the right to
    // hold a handle, so this neither locks a file nor needs read permission on it.
    private static string? Final(string path)
    {
        IntPtr handle = InvalidHandle;
        try
        {
            handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle == InvalidHandle) return null;

            var buffer = new StringBuilder(1024);
            int length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, VolumeNameDos);
            if (length == 0) return null;
            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder(length + 1);
                length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, VolumeNameDos);
                if (length == 0) return null;
            }

            return Strip(buffer.ToString());
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally
        {
            if (handle != InvalidHandle) CloseHandle(handle);
        }
    }

    /// <summary>
    /// Drops the extended-length prefix the API always returns. <see cref="PathScope"/> refuses that
    /// form outright, so a resolved path that kept it would be judged invalid rather than compared.
    /// </summary>
    public static string Strip(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, int dwDesiredAccess, int dwShareMode, IntPtr lpSecurityAttributes,
        int dwCreationDisposition, int dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandleW(
        IntPtr hFile, StringBuilder lpszFilePath, int cchFilePath, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
