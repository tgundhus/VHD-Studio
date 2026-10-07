using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VhdAttachCommon;

namespace VhdAttach {

    /// <summary>
    /// Update checks and bug reports against this project's GitHub repository.
    /// Nothing is sent automatically; reports open a pre-filled issue in the browser for the user to review.
    /// </summary>
    internal static class Feedback {

        private const string LatestReleaseApi = "https://api.github.com/repos/tgundhus/VhdAttach/releases/latest";

        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;


        /// <summary>
        /// Returns the newer release tag (e.g. "v5.1.0") or null if there is none or the check failed.
        /// </summary>
        public static async Task<string> GetNewerReleaseAsync(CancellationToken cancellationToken) {
            try {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) }) {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("VhdStudio/" + CurrentVersion);
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    using (var response = await client.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false)) {
                        if (!response.IsSuccessStatusCode) { return null; }
                        using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                        using (var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)) {
                            var tag = json.RootElement.GetProperty("tag_name").GetString();
                            return IsNewer(tag, CurrentVersion) ? tag : null;
                        }
                    }
                }
            } catch (HttpRequestException) {
            } catch (TaskCanceledException) {
            } catch (JsonException) {
            } catch (InvalidOperationException) { }
            return null;
        }

        internal static bool IsNewer(string tag, Version current) {
            if (string.IsNullOrEmpty(tag)) { return false; }
            var text = tag.TrimStart('v', 'V');
            var dash = text.IndexOf('-');
            if (dash >= 0) { text = text.Substring(0, dash); } //pre-release suffix
            if (!Version.TryParse(text, out var release)) { return false; }
            return Normalize(release) > Normalize(current);
        }

        private static Version Normalize(Version v) {
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        public static void OpenReleases() {
            OpenUrl(Branding.ReleasesUrl);
        }

        public static void OpenProjectPage() {
            OpenUrl(Branding.ProjectUrl);
        }

        /// <summary>
        /// Opens a pre-filled GitHub issue. The user sees and edits everything before submitting.
        /// </summary>
        public static void ReportIssue(Exception exception = null) {
            var title = (exception == null) ? "" : exception.GetType().Name + ": " + Truncate(exception.Message, 120);
            var body = new StringBuilder();
            body.AppendLine("**What happened?**");
            body.AppendLine();
            body.AppendLine();
            body.AppendLine("**Environment**");
            body.AppendLine("- " + Branding.ProductName + " " + CurrentVersion);
            body.AppendLine("- " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)"));
            body.AppendLine("- .NET " + Environment.Version);
            if (exception != null) {
                body.AppendLine();
                body.AppendLine("**Exception**");
                body.AppendLine("```");
                body.AppendLine(Truncate(exception.ToString(), 4000));
                body.AppendLine("```");
            }
            var url = Branding.IssuesUrl + "/new?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body.ToString());
            OpenUrl(url);
        }

        public static void ShowUnhandledException(IWin32Window owner, Exception exception) {
            var text = "An unexpected error occurred.\n\n" + exception.Message + "\n\nDo you want to open a pre-filled bug report on GitHub? You can review it before it is submitted.";
            if (Medo.MessageBox.ShowError(owner, text, MessageBoxButtons.YesNo) == DialogResult.Yes) {
                ReportIssue(exception);
            }
        }

        private static string Truncate(string text, int length) {
            return (text.Length <= length) ? text : text.Substring(0, length) + "…";
        }

        private static void OpenUrl(string url) {
            try {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            } catch (System.ComponentModel.Win32Exception) { }
        }

    }
}
