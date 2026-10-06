namespace VhdAttach {
    internal class PipeResponse {

        public PipeResponse(bool isError, string message, string errorCode = null) {
            this.IsError = isError;
            this.Message = message;
            this.ErrorCode = errorCode;
        }

        public bool IsError { get; private set; }
        public string Message { get; private set; }

        /// <summary>
        /// Machine-readable error kind (e.g. "InUse") or null.
        /// </summary>
        public string ErrorCode { get; private set; }

        public bool IsInUse => this.IsError && (this.ErrorCode == "InUse");

    }
}
