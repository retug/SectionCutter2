using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ETABSv1;

namespace SectionCutter
{
    class cPlugin
    {
        private static readonly object _sync = new object();

        private static Thread _uiThread;
        private static Dispatcher _uiDispatcher;

        private static MainWindow _mainWindow;

        public void Main(ref cSapModel SapModel, ref cPluginCallback ISapPlugin)
        {
            var localSapModel = SapModel;
            var localPlugin = ISapPlugin;

            EnsureUiThreadStarted();

            // Always marshal to the UI dispatcher thread
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                ShowOrActivateWindow(localSapModel, localPlugin);
            }));
        }

        private static void EnsureUiThreadStarted()
        {
            lock (_sync)
            {
                if (_uiThread != null && _uiThread.IsAlive && _uiDispatcher != null)
                    return;

                _uiThread = new Thread(() =>
                {
                    // Create dispatcher for this STA thread
                    _uiDispatcher = Dispatcher.CurrentDispatcher;

                    // Start message loop for this thread
                    Dispatcher.Run();
                });

                _uiThread.SetApartmentState(ApartmentState.STA);
                _uiThread.IsBackground = true;
                _uiThread.Start();

                // Wait until dispatcher is ready
                while (_uiDispatcher == null)
                    Thread.Sleep(10);
            }
        }

        private static void ShowOrActivateWindow(cSapModel sapModel, cPluginCallback plugin)
        {
            // If the old window exists, but is closed/not loaded, drop it
            if (_mainWindow != null && !_mainWindow.IsLoaded)
                _mainWindow = null;

            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow(sapModel, plugin);

                // When closed, allow reopening later
                _mainWindow.Closed += (_, __) =>
                {
                    _mainWindow = null;
                };

                _mainWindow.Show();
                _mainWindow.Activate();
                return;
            }

            // Bring to front if already open
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        }

        public long Info(ref string Text)
        {
            Text = "Section Cutter Tool. Diaphragm Slicer.";
            return 0;
        }
    }
}
