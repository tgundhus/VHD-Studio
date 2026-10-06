using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Windows.Forms;

namespace VhdAttachService {

    internal static class App {

        [STAThread()]
        static void Main() {
            System.AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            if (Medo.Application.Args.Current.ContainsKey("Interactive")) {

                Tray.Show();
                Service.Start();
                Tray.SetStatusToRunningInteractive();
                Application.Run();
                Service.Stop();
                Tray.Hide();
                Environment.Exit(0);

            } else if (Medo.Application.Args.Current.ContainsKey("Install")) {

                try {
                    ServiceInstaller.Install(Environment.ProcessPath);
                    System.Environment.Exit(0);
                } catch (Exception ex) {
                    Trace.TraceError("Cannot install service: " + ex.Message);
                    System.Environment.Exit(1);
                }

            } else if (Medo.Application.Args.Current.ContainsKey("Uninstall")) {

                try {
                    System.Environment.Exit(ServiceInstaller.Uninstall() ? 0 : 1);
                } catch (Exception ex) { //no service with that name
                    Trace.TraceError("Cannot uninstall service: " + ex.Message);
                    System.Environment.Exit(1);
                }

            } else if (Medo.Application.Args.Current.ContainsKey("Start")) {

                try {
                    using (var service = new ServiceController(AppService.Instance.ServiceName)) {
                        if (service.Status != ServiceControllerStatus.Running) {
                            service.Start();
                            service.WaitForStatus(ServiceControllerStatus.Running, new TimeSpan(0, 0, 1));
                        }
                    }
                } catch (Exception) { }
                System.Environment.Exit(0);

            } else {

                if (Environment.UserInteractive) {
                    Tray.Show();
                    ServiceStatusThread.Start();
                    Application.Run();
                    ServiceStatusThread.Stop();
                    Tray.Hide();
                    Environment.Exit(0);
                } else {
                    ServiceBase.Run(new ServiceBase[] { AppService.Instance });
                }

            }
        }


        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
            Medo.Diagnostics.ErrorReport.SaveToTemp(e.ExceptionObject as Exception);
            AppService.Instance.ExitCode = 1064; //ERROR_EXCEPTION_IN_SERVICE
            AppService.Instance.AutoLog = false;
            System.Threading.Thread.Sleep(1000); //just to sort it properly in event log.
            Environment.Exit(AppService.Instance.ExitCode);
        }

    }

}
