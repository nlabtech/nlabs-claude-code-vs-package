using System;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// Command identifiers, mirrored from VSCommandTable.vsct by hand. The command set
    /// GUID and the ids MUST match the .vsct symbols exactly; a mismatch means the
    /// handler is registered for a command that the menu never shows, with no error.
    /// </summary>
    internal static class PackageGuids
    {
        public const string CommandSetString = "7f3a1c22-9b64-4d1e-8a5f-2c9e6d4b1a30";
        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    internal static class PackageIds
    {
        public const int RestartBridgeCommandId = 0x0100;
        public const int SendSelectionCommandId = 0x0101;
    }
}
