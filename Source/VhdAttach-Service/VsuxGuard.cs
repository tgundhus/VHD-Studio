using System;
using System.IO;

namespace VhdAttachCommon {
    /// <summary>Keep password containers out of ordinary service and boot-time mount paths.</summary>
    internal static class VsuxGuard {
        public static bool IsVsux(string path) => string.Equals(Path.GetExtension(path), ".vsux", StringComparison.OrdinalIgnoreCase);

        public static void RefuseOrdinaryRoute(string path) {
            if (IsVsux(path)) {
                throw new InvalidOperationException("VSUX requires the password prompt in VHD Studio. Use Attach or Detach & lock on the .vsux file; automatic mounting and forced detachment are unavailable.");
            }
        }
    }
}
