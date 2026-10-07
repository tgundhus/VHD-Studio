using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace VhdAttach {
    internal partial class ServiceWaitForm : Form {

        private readonly string ErrorTitle;

        /// <param name="errorTitle">Shown with the error; null means the action talks to the service.</param>
        public ServiceWaitForm(string title, Action action, string errorTitle = null) {
            this.ErrorTitle = errorTitle;
            InitializeComponent();
            this.Font = SystemFonts.MessageBoxFont;
            this.ControlBox = false;

            this.Text = title;
            bw.RunWorkerAsync(action);
        }


        private void bw_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e) {
            var exceptions = new List<Exception>();
            var action = (Delegate)e.Argument;
            try {
                action.DynamicInvoke();
            } catch (TargetInvocationException ex) {
                if (ex.InnerException != null) {
                    throw ex.InnerException;
                } else {
                    throw;
                }
            }
        }

        private void bw_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e) {
            if (e.Error == null) {
                this.DialogResult = DialogResult.OK;
            } else {
                if (this.ErrorTitle == null) {
                    Messages.ShowServiceIOException(this, e.Error);
                } else {
                    Medo.MessageBox.ShowError(this, this.ErrorTitle + "\n\n" + e.Error.Message);
                }
                this.DialogResult = DialogResult.Cancel;
            }
        }

    }
}
