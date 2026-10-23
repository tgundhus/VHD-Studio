using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace VhdAttach {
    internal static class App {

        [STAThread]
        static void Main() {
            bool createdNew;
            var mutexSecurity = new MutexSecurity();
            mutexSecurity.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
            using (var setupMutex = MutexAcl.Create(false, @"Global\VhdStudio", out createdNew, mutexSecurity)) { //used by setup to detect running instances
                System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                Medo.Application.UnhandledCatch.ThreadException += new EventHandler<ThreadExceptionEventArgs>(UnhandledCatch_ThreadException);
                Medo.Application.UnhandledCatch.Attach();

                if (!((Environment.OSVersion.Version.Build < 7000) || (App.IsRunningOnMono))) {
                    var appId = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory.FullName;
                    if (appId.Length > 127) { appId = @"VhdStudio\" + appId.Substring(appId.Length - 127 - 20); }
                    NativeMethods.SetCurrentProcessExplicitAppUserModelID(appId);
                } else {
                    Medo.MessageBox.ShowError(null, "This program requires Windows 7 or later.");
                    System.Environment.Exit(1);
                }

                Medo.Windows.Forms.TaskbarProgress.DoNotThrowNotImplementedException = true;

                if (TryRunVsuxOperation()) { return; }


                if (Medo.Application.Args.Current.ContainsKey("DiskManager")) {
                    var diskNumber = Medo.Application.Args.Current.GetValue("Disk", -1);
                    Application.Run(new Storage.DiskManagerForm((diskNumber >= 0) ? diskNumber : (int?)null) { StartPosition = FormStartPosition.CenterScreen });
                    return;
                }

                if (Medo.Application.Args.Current.ContainsKey("Maintain")) {
                    var maintainFiles = Medo.Application.Args.Current.GetValues("");
                    if (maintainFiles.Length == 0) { System.Environment.Exit(1); }
                    if (VhdStudio.Vsux.VsuxOperations.IsVsuxPath(maintainFiles[0].TrimEnd('\"'))) {
                        using (var form = new VsuxToolsForm(maintainFiles[0].TrimEnd('\"')) { StartPosition = FormStartPosition.CenterScreen }) {
                            Application.Run(form);
                            if (form.ResultFileName != null) {
                                Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, VhdAttachCommon.Branding.ApplicationExe), Ui.Quote(form.ResultFileName)) { UseShellExecute = true });
                            }
                        }
                        return;
                    }
                    using (var form = new MaintenanceForm(maintainFiles[0].TrimEnd('\"'), Medo.Application.Args.Current.GetValue("Task", null)) { StartPosition = FormStartPosition.CenterScreen }) {
                        Application.Run(form);
                        if (form.ProtectedFileName != null) {
                            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, VhdAttachCommon.Branding.ApplicationExe), Ui.Quote(form.ProtectedFileName)) { UseShellExecute = true });
                        }
                    }
                    return;
                }

                bool doAttach = Medo.Application.Args.Current.ContainsKey("Attach");
                bool doDetach = Medo.Application.Args.Current.ContainsKey("Detach") && (!doAttach);
                bool doDetachDrive = Medo.Application.Args.Current.ContainsKey("DetachDrive") && (!doAttach) && (!doDetach);
                bool doChangeLetter = Medo.Application.Args.Current.ContainsKey("ChangeLetter") && (!doAttach) && (!doDetach) && (!doDetachDrive);

                bool doAnything = doAttach || doDetach || doDetachDrive || doChangeLetter;

                if (doAnything) {

                    string[] argfiles = Medo.Application.Args.Current.GetValues("");

                    if (doChangeLetter) {
                        CommandLineAddon cla = new CommandLineAddon();
                        int res = cla.ChangeDriveLetter(argfiles);
                        System.Environment.Exit(res);
                        return;
                    }
                   
                    var files = new List<FileInfo>();
                    foreach (var iFile in argfiles) {
                        files.Add(new FileInfo(iFile.TrimEnd(new char[] { '\"' })));
                    }

                    if (files.Count == 0) {
                        System.Environment.Exit(1);
                        return;
                    }

                    Form appForm = null;
                    if (doAttach) {
                        appForm = new AttachForm(files, Medo.Application.Args.Current.ContainsKey("readonly"), false);
                    } else if (doDetach) {
                        appForm = new DetachForm(files);
                    } else if (doDetachDrive) {
                        appForm = new DetachDriveForm(files);
                    }

                    if (appForm != null) {
                        Medo.Windows.Forms.TaskbarProgress.DefaultOwner = appForm;
                        Application.Run(appForm);
                        System.Environment.Exit(System.Environment.ExitCode);
                    } else {
                        System.Environment.Exit(1);
                    }

                } else { //open localy

                    Application.Run(new MainForm());

                }
            }
        }

        private static bool TryRunVsuxOperation() {
            string operation = null;
            foreach (var name in new[] { "Attach", "Lock", "Protect", "Create", "CreateDisk", "Portable", "Convert", "Backup" }) {
                if (!Medo.Application.Args.Current.ContainsKey("Vsux" + name)) { continue; }
                if (operation != null) {
                    Medo.MessageBox.ShowError(null, "Specify only one VSUX operation.");
                    Environment.ExitCode = 1;
                    return true;
                }
                operation = name;
            }
            if (operation == null) { return false; }
            var files = Medo.Application.Args.Current.GetValues("");
            var hasDestination = operation == "Protect" || operation == "Convert" || operation == "Backup";
            if (files.Length != (hasDestination ? 2 : 1)) {
                Medo.MessageBox.ShowError(null, "Specify the source file and, for protection, conversion or export, a new destination path.");
                Environment.ExitCode = 1;
                return true;
            }
            long sizeInBytes = 0;
            if ((operation == "Create" || operation == "CreateDisk") && (!long.TryParse(Medo.Application.Args.Current.GetValue("size", ""), NumberStyles.None, CultureInfo.InvariantCulture, out sizeInBytes)
                || sizeInBytes < 8L * 1024 * 1024 || sizeInBytes % 4096 != 0)) {
                Medo.MessageBox.ShowError(null, "Specify a disk size in bytes, at least 8 MiB and aligned to 4096 bytes.");
                Environment.ExitCode = 1;
                return true;
            }
            var result = VsuxForm.RunOperation(null, operation, files[0].TrimEnd('\"'),
                Medo.Application.Args.Current.ContainsKey("readonly"), hasDestination ? files[1].TrimEnd('\"') : null,
                sizeInBytes, Medo.Application.Args.Current.ContainsKey("fixed"), Medo.Application.Args.Current.ContainsKey("singlepartition"));
            Environment.ExitCode = result == DialogResult.OK ? 0 : result == DialogResult.Cancel ? 2 : 1;
            return true;
        }



        private static void UnhandledCatch_ThreadException(object sender, ThreadExceptionEventArgs e) {
#if !DEBUG
            Feedback.ShowUnhandledException(null, e.Exception);
#else
            throw e.Exception;
#endif
        }


        private static class NativeMethods {

            [DllImport("Shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern UInt32 SetCurrentProcessExplicitAppUserModelID(String AppID);

        }

        private static bool IsRunningOnMono {
            get {
                return (Type.GetType("Mono.Runtime") != null);
            }
        }

    }
}
