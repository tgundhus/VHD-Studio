namespace VhdAttachCommon {

    /// <summary>
    /// Product identity shared by the application and the service.
    /// </summary>
    internal static class Branding {

        public const string ProductName = "VHD Studio";
        public const string Publisher = "xGND Software";
        public const string Author = "Tobias Gundhus";

        public const string ApplicationExe = "VhdStudio.exe";
        public const string ServiceExe = "VhdStudioService.exe";

        public const string ServiceName = "VhdStudio";
        public const string LegacyServiceName = "VhdAttach";

        public const string PipeName = "VhdStudio-Commands";
        public const string PacketProduct = "VhdStudio";

        public const string SettingsSubkeyPath = @"Software\xGND Software\VHD Studio"; //HKCU settings use the same path (AssemblyCompany\Product)
        public const string LegacySettingsSubkeyPath = @"Software\Josip Medved\VHD Attach";

        public const string ProjectUrl = "https://github.com/tgundhus/VhdAttach";
        public const string IssuesUrl = "https://github.com/tgundhus/VhdAttach/issues";
        public const string ReleasesUrl = "https://github.com/tgundhus/VhdAttach/releases";

    }

}
