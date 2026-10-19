using System;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>GUIDs and command ids; must match ClaudeLimitsPackage.vsct.</summary>
    internal static class PackageGuids
    {
        public const string PackageString = "09EED115-62AD-4383-A1D6-49B3E2884003";
        public const string CommandSetString = "13DF8A66-5FAC-4C20-A074-040D3E40EC4B";
        public const string ToolWindowString = "314B6E4D-97BB-4B38-AE7A-65181760D2FA";
        public const string OptionsPageString = "78052FC4-C102-42F9-BDDF-80F61BFFE786";

        public static readonly Guid Package = new Guid(PackageString);
        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    internal static class PackageIds
    {
        public const int ShowToolWindowViewCommand = 0x0100;
        public const int ShowToolWindowToolsCommand = 0x0101;
        public const int RefreshCommand = 0x0102;
    }
}
