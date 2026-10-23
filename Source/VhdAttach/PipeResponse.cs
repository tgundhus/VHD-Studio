namespace VhdAttach {
    internal class PipeResponse {

        public PipeResponse(bool isError, string message, string errorCode = null, string driveLetters = null) {
            this.IsError = isError;
            this.Message = message;
            this.ErrorCode = errorCode;
            this.DriveLetters = driveLetters;
        }

        public bool IsError { get; private set; }
        public string Message { get; private set; }

        /// <summary>
        /// Machine-readable error kind (e.g. "InUse") or null.
        /// </summary>
        public string ErrorCode { get; private set; }

        public bool IsInUse => this.IsError && (this.ErrorCode == "InUse");

        /// <summary>
        /// Drive letter notices of an attach, e.g. "inuse:Z:G;taken:E:F", or null (see VhdAttach.DriveLetters.FormatNotices).
        /// </summary>
        public string DriveLetters { get; private set; }

    }
}
