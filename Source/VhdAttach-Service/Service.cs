using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VhdAttachCommon;

namespace VhdAttachService {

    internal static class Service {

        private static Thread _thread;
        private static ManualResetEvent _cancelEvent;


        public static void Start() {
            if (_thread != null) { return; }

            _cancelEvent = new ManualResetEvent(false);
            _thread = new Thread(Run);
            _thread.Name = "Service";
            _thread.IsBackground = true; //never keep the process (and its files) alive after the service stopped
            _thread.Start();
            Debug.WriteLine("Service thread started.");
        }

        public static void Stop() {
            Debug.WriteLine("Service thread stopping...");
            try {
                _cancelEvent.Set();
                PipeServer.Pipe.Close();
                NativeMethods.DeleteFile(PipeServer.Pipe.FullPipeName); //I have no idea why exactly this unblocks ConnectNamedPipe...
                if (!_thread.Join(5000)) { //Thread.Abort does not exist on .NET; the thread is a background thread and ends with the process
                    Debug.WriteLine("Service thread did not stop in time; process exit will end it.");
                }
                _thread = null;
            } catch { }
            Debug.WriteLine("Service thread stoped.");
        }

        private static void Run() {
            try {
                AuditLog.Succeeded("Service started", Environment.MachineName); //creates the protected log folder as early as possible
                ThreadPool.QueueUserWorkItem(new WaitCallback(RunAttachAutomatics));

                try {
                    PipeServer.Start();
                    while (!_cancelEvent.WaitOne(0, false)) {
                        try {
                            var response = PipeServer.Receive(_cancelEvent);
                            if (response != null) {
                                PipeServer.Reply(response);
                            }
                        } catch (Exception ex) {
                            if (_cancelEvent.WaitOne(0, false)) { return; }
                            Debug.WriteLine(ex.Message);
                            PipeServer.Stop();
                            PipeServer.Start();
                            Thread.Sleep(50);
                        }
                    }
                } finally {
                    Debug.WriteLine("AppServiceThread.Run: Finally.");
                    PipeServer.Stop();
                }

            } catch (ThreadAbortException) {
                Debug.WriteLine("AppServiceThread.Run: Thread aborted.");
            }
        }

        /// <summary>
        /// Auto-attach runs once per boot. When the service is merely restarted (upgrade, manual restart),
        /// disks keep their state: attachments survive service restarts (permanent lifetime), and disks the
        /// user detached on purpose must not reappear.
        /// </summary>
        private static void RunAttachAutomatics(Object stateInfo) {
            var bootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var lastRun = ServiceSettings.LastAutoAttachBoot;
            if ((lastRun != null) && (Math.Abs((bootTime - lastRun.Value).TotalMinutes) < 2)) {
                AuditLog.Succeeded("Auto-attach skipped", "already ran since boot " + bootTime.ToLocalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            ServiceSettings.LastAutoAttachBoot = bootTime;

            var todoList = new List<FileWithOptions>(ServiceSettings.AutoAttachVhdList);
            var failedList = new List<FileWithOptions>();

            for (int i = 0; i < 5; i++) {
                AttachAutomatics(todoList, failedList);
                if (failedList.Count == 0) { break; } //no failed
                todoList.Clear();
                todoList.AddRange(failedList); //repeat only failed
                failedList.Clear();
                Thread.Sleep(1000 + i * 2 * 1000); //give it a bit more time
            }
        }

        private static void AttachAutomatics(List<FileWithOptions> todoList, List<FileWithOptions> failedList) {
            foreach (var fwo in todoList) {
                try {
                    Thread.Sleep(1000); //a bit of breather
                    if (VirtualDiskImage.IsAttached(fwo.FileName)) { //e.g. first start after installing over an older version
                        AuditLog.Succeeded("Auto-attach", fwo.ToString(), "already attached");
                        continue;
                    }
                    using (var service = PipeCaller.ForService()) { //entries were access-checked when saved; at boot paths are re-pinned (no links, no hard links)
                        AuditLog.Started("Auto-attach", fwo.ToString());
                        AttachHelper.Attach(fwo, service, initializeDisk: false, strictPaths: true);
                        AuditLog.Succeeded("Auto-attach", fwo.ToString());
                    }

                } catch (Exception ex) {
                    if (failedList != null) { failedList.Add(fwo); }
                    Trace.TraceError("E: Cannot attach file \"" + fwo.FileName + "\". " + ex.Message);
                    AuditLog.Failed("Auto-attach", fwo.ToString(), ex);
                    Medo.Diagnostics.ErrorReport.SaveToTemp(ex, fwo.FileName);
                }
            }
        }



        private static class NativeMethods {

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern Boolean DeleteFile(String lpFileName);

        }

    }

}
