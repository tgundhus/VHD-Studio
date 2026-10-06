using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VhdAttach.Storage {

    /// <summary>
    /// Small code-built dialog used for confirmations and simple inputs in the storage tools.
    /// </summary>
    internal sealed class PromptForm : Form {

        private readonly TableLayoutPanel Table;
        private readonly Dictionary<string, Control> Fields = new Dictionary<string, Control>();
        private readonly Button OkButton;
        private string RequiredConfirmation;
        private TextBox ConfirmationBox;

        public PromptForm(string title, string message, string command = null, bool destructive = false) {
            this.Text = title;
            this.Font = SystemFonts.MessageBoxFont;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.AutoSize = true;
            this.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            this.Padding = new Padding(this.Font.Height / 2);

            this.Table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(this.Font.Height / 2) };
            this.Table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            this.Table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            this.Controls.Add(this.Table);

            var maxWidth = this.Font.Height * 32;
            var messageLabel = new Label { Text = message, AutoSize = true, MaximumSize = new Size(maxWidth, 0), Margin = new Padding(0, 0, 0, this.Font.Height / 2) };
            if (destructive) { messageLabel.ForeColor = Ui.Danger; }
            this.AddFullRow(messageLabel);

            if (!string.IsNullOrEmpty(command)) {
                var commandBox = new TextBox {
                    Text = command, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, Font = new Font(FontFamily.GenericMonospace, this.Font.SizeInPoints),
                    Width = maxWidth, BackColor = Ui.AccentSoft, Margin = new Padding(0, 0, 0, this.Font.Height / 2),
                };
                this.AddFullRow(new Label { Text = "Equivalent PowerShell:", AutoSize = true, ForeColor = Ui.Muted });
                this.AddFullRow(commandBox);
            }

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, this.Font.Height / 2, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            this.OkButton = new Button { Text = destructive ? "Yes, do it" : "OK", DialogResult = DialogResult.OK, AutoSize = true };
            if (destructive) { this.OkButton.ForeColor = Ui.Danger; }
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(this.OkButton);
            this.ButtonRow = buttons;
            this.AcceptButton = this.OkButton;
            this.CancelButton = cancel;
        }

        private readonly FlowLayoutPanel ButtonRow;

        protected override void OnLoad(EventArgs e) {
            this.AddFullRow(this.ButtonRow);
            base.OnLoad(e);
        }

        private void AddFullRow(Control control) {
            this.Table.Controls.Add(control);
            this.Table.SetColumnSpan(control, 2);
        }

        /// <summary>
        /// User has to type text exactly (e.g. "Disk 3") before OK is enabled.
        /// </summary>
        public PromptForm RequireTyping(string text) {
            this.RequiredConfirmation = text;
            this.AddFullRow(new Label { Text = string.Format("Type \"{0}\" to confirm:", text), AutoSize = true, Margin = new Padding(0, this.Font.Height / 2, 0, 0) });
            this.ConfirmationBox = new TextBox { Width = this.Font.Height * 12 };
            this.ConfirmationBox.TextChanged += (s, e) => { this.OkButton.Enabled = string.Equals(this.ConfirmationBox.Text.Trim(), this.RequiredConfirmation, StringComparison.OrdinalIgnoreCase); };
            this.AddFullRow(this.ConfirmationBox);
            this.OkButton.Enabled = false;
            return this;
        }

        public PromptForm AddText(string key, string label, string value = "") {
            var box = new TextBox { Text = value, Width = this.Font.Height * 18 };
            return this.AddField(key, label, box);
        }

        public PromptForm AddChoice(string key, string label, string[] items, string selected) {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = this.Font.Height * 18 };
            combo.Items.AddRange(items);
            combo.SelectedItem = selected;
            if (combo.SelectedIndex < 0 && combo.Items.Count > 0) { combo.SelectedIndex = 0; }
            return this.AddField(key, label, combo);
        }

        public PromptForm AddCheck(string key, string label, bool value) {
            var check = new CheckBox { Text = label, Checked = value, AutoSize = true };
            this.Fields[key] = check;
            this.AddFullRow(check);
            return this;
        }

        private PromptForm AddField(string key, string label, Control control) {
            this.Fields[key] = control;
            this.Table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, this.Font.Height / 2, 0) });
            this.Table.Controls.Add(control);
            return this;
        }

        public string GetText(string key) => (this.Fields[key] as TextBox)?.Text?.Trim() ?? (this.Fields[key] as ComboBox)?.SelectedItem as string;
        public bool GetCheck(string key) => (this.Fields[key] as CheckBox)?.Checked ?? false;

    }
}
