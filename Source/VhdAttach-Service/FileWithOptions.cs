using System;
using System.Collections.Generic;
using System.Text;

namespace VhdAttachCommon {
    internal class FileWithOptions {

        public FileWithOptions(string fileNameWithOptions) {
            if (fileNameWithOptions.StartsWith("/")) {
                /*
                 * Each file can have additional settings area that starts with / and ends with next /.
                 * E.g. "/readonly,nodriveletter/D:\Test.vhd" or "/mount=C%3A%5CMounts%5CData/D:\Test.vhdx"
                 */
                var iEndPipe = fileNameWithOptions.IndexOf("/", 1);
                var additionalSettings = fileNameWithOptions.Substring(1, iEndPipe - 1);
                this.FileName = fileNameWithOptions.Substring(iEndPipe + 1);
                foreach (var setting in additionalSettings.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                    var iEquals = setting.IndexOf('=');
                    var key = (iEquals < 0) ? setting : setting.Substring(0, iEquals);
                    var value = (iEquals < 0) ? null : Uri.UnescapeDataString(setting.Substring(iEquals + 1));
                    switch (key.Trim().ToUpperInvariant()) {
                        case "READONLY": this.ReadOnly = true; break;
                        case "NODRIVELETTER": this.NoDriveLetter = true; break;
                        case "MOUNT": this.MountFolder = string.IsNullOrEmpty(value) ? null : value; break;
                    }
                }
            } else {
                this.FileName = fileNameWithOptions;
            }
        }

        public string FileName { get; private set; }
        public bool ReadOnly { get; set; }
        public bool NoDriveLetter { get; set; }

        /// <summary>
        /// Gets/sets empty folder to which the first volume is mounted after attach (null if not used).
        /// </summary>
        public string MountFolder { get; set; }

        public override string ToString() {
            var options = new List<string>();
            if (this.ReadOnly) { options.Add("readonly"); }
            if (this.NoDriveLetter) { options.Add("nodriveletter"); }
            if (!string.IsNullOrEmpty(this.MountFolder)) { options.Add("mount=" + Uri.EscapeDataString(this.MountFolder)); }

            var sb = new StringBuilder();
            if (options.Count >= 1) {
                sb.Append("/");
                sb.Append(string.Join(",", options));
                sb.Append("/");
            }
            sb.Append(this.FileName);
            return sb.ToString();
        }

    }
}
