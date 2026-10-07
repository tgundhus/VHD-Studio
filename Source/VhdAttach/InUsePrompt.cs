using System.Windows.Forms;

namespace VhdAttach {

    internal enum InUseChoice { Retry, Force, Cancel }

    internal static class InUsePrompt {

        /// <summary>
        /// Asks what to do when files on a disk are still open. Detaching anyway is never the default.
        /// </summary>
        public static InUseChoice Ask(IWin32Window owner, string diskName, string message) {
            using (var form = new Storage.PromptForm("Disk in use", string.Format("{0}\n\n{1}\n\nRetry after closing the programs, or detach anyway (unsaved data in open files will be lost).", diskName, message), null, destructive: true)) {
                form.AddChoice("choice", "Action:", new[] { "Retry", "Detach anyway (may lose unsaved data)" }, "Retry");
                if (form.ShowDialog(owner) != DialogResult.OK) { return InUseChoice.Cancel; }
                return form.GetText("choice") == "Retry" ? InUseChoice.Retry : InUseChoice.Force;
            }
        }

    }
}
