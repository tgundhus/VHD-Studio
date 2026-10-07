using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Security.Principal;
using System.Windows.Forms;

namespace VhdAttach {

    /// <summary>
    /// VHD Studio look and feel shared by the newer windows.
    /// </summary>
    internal static class Ui {

        public static readonly Color Accent = Color.FromArgb(0x1F, 0x5F, 0xD1);       //VHD Studio blue
        public static readonly Color AccentDark = Color.FromArgb(0x16, 0x3F, 0x8F);
        public static readonly Color AccentSoft = Color.FromArgb(0xE8, 0xF0, 0xFC);
        public static readonly Color Teal = Color.FromArgb(0x0F, 0x9D, 0x8A);
        public static readonly Color Danger = Color.FromArgb(0xC4, 0x2B, 0x1C);
        public static readonly Color Muted = Color.FromArgb(0x60, 0x66, 0x70);

        private static Icon _appIcon;
        /// <summary>
        /// Application icon embedded in the executable.
        /// </summary>
        public static Icon AppIcon => _appIcon ??= Icon.ExtractAssociatedIcon(Environment.ProcessPath);

        #region Glyphs

        /// <summary>
        /// Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10) code points.
        /// </summary>
        public static class Glyph {
            public const char Disk = '';
            public const char Wrench = '';
            public const char Compact = '';      //back to window / shrink
            public const char Resize = '';       //full screen / grow
            public const char Convert = '';
            public const char Branch = '';       //folder
            public const char Merge = '';
            public const char Repair = '';
            public const char Info = '';
            public const char Shield = '';
            public const char Refresh = '';
            public const char Add = '';
            public const char Delete = '';
            public const char Format = '';
            public const char Letter = '';
            public const char More = '';
            public const char Warning = '';
            public const char Github = '';
        }

        private static string GlyphFontName;

        public static Bitmap GetGlyph(char glyph, int size, Color color) {
            if (GlyphFontName == null) {
                GlyphFontName = "Segoe MDL2 Assets";
                using (var fonts = new InstalledFontCollection()) {
                    foreach (var family in fonts.Families) {
                        if (family.Name == "Segoe Fluent Icons") { GlyphFontName = family.Name; break; }
                    }
                }
            }
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font(GlyphFontName, size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
            }
            return bitmap;
        }

        public static int ScaledIconSize(Control control, int logicalSize = 16) {
            return (int)Math.Round(logicalSize * control.DeviceDpi / 96.0);
        }

        #endregion


        #region Elevation

        public static bool IsElevated {
            get {
                using (var identity = WindowsIdentity.GetCurrent()) {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }

        /// <summary>
        /// Starts this executable elevated (UAC prompt) with given arguments and waits for it to finish.
        /// Returns false if the user declined the prompt.
        /// </summary>
        public static bool RunElevated(string arguments, bool wait) {
            try {
                var startInfo = new ProcessStartInfo(Environment.ProcessPath, arguments) { UseShellExecute = true, Verb = "runas" };
                using (var process = Process.Start(startInfo)) {
                    if (wait) { process?.WaitForExit(); }
                }
                return true;
            } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { //ERROR_CANCELLED
                return false;
            }
        }

        public static string Quote(string path) {
            return "\"" + path.TrimEnd('\\') + "\"";
        }

        #endregion


        #region Controls

        /// <summary>
        /// Branded banner used at the top of the VHD Studio tool windows.
        /// </summary>
        public sealed class Banner : Control {
            public Banner(string title, string subtitle) {
                this.Title = title;
                this.Subtitle = subtitle;
                this.Dock = DockStyle.Top;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                this.UpdateHeight();
            }

            private void UpdateHeight() {
                this.Height = (int)(this.Font.Height * 3.4);
            }

            protected override void OnParentChanged(EventArgs e) {
                base.OnParentChanged(e);
                this.UpdateHeight();
            }

            public string Title { get; set; }
            public string Subtitle { get; set; }

            protected override void OnFontChanged(EventArgs e) {
                base.OnFontChanged(e);
                this.UpdateHeight();
            }

            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var brush = new LinearGradientBrush(this.ClientRectangle, AccentDark, Accent, LinearGradientMode.Horizontal)) {
                    g.FillRectangle(brush, this.ClientRectangle);
                }
                var pad = this.Font.Height;
                var iconSize = (int)(this.Height * 0.55);
                using (var icon = GetGlyph(Glyph.Disk, iconSize, Color.White)) {
                    g.DrawImage(icon, pad, (this.Height - iconSize) / 2);
                }
                var x = pad * 2 + iconSize;
                using (var titleFont = new Font(this.Font.FontFamily, this.Font.Size * 1.45f, FontStyle.Bold))
                using (var subtitleBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) {
                    var titleHeight = titleFont.Height;
                    var top = (this.Height - titleHeight - this.Font.Height) / 2;
                    TextRenderer.DrawText(g, this.Title, titleFont, new Point(x, top), Color.White, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, this.Subtitle, this.Font, new Point(x, top + titleHeight), Color.FromArgb(225, 235, 250), TextFormatFlags.NoPadding);
                }
            }
        }

        public static string FormatSize(long bytes) {
            return Medo.Extensions.BinaryPrefixExtensions.ToBinaryPrefixString(bytes, "B", "0.0");
        }

        public static string FormatSize(long? bytes) {
            return bytes.HasValue ? FormatSize(bytes.Value) : "";
        }

        public static bool TryParseSize(string text, out long bytes) {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            text = text.Trim().ToUpperInvariant().Replace("IB", "B");
            long multiplier = 1;
            foreach (var (suffix, value) in new[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10), ("B", 1L) }) {
                if (text.EndsWith(suffix, StringComparison.Ordinal)) {
                    multiplier = value;
                    text = text.Substring(0, text.Length - suffix.Length).Trim();
                    break;
                }
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) { return false; }
            if (number <= 0) { return false; }
            bytes = (long)(number * multiplier);
            return true;
        }

        #endregion

    }
}
