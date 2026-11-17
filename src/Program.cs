using System;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace AvdHealthCheck
{
    public static class Program
    {
        [STAThread]
        public static int Main()
        {
            // .NET Framework allows 2 connections per host by default; the speed test needs more.
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += OnUnhandled;

            try
            {
                var controller = new MainController();
                return app.Run(controller.Window);
            }
            catch (Exception ex)
            {
                MessageBox.Show("AVD Health Check could not start.\n\n" + ex, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
