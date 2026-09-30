using System.Threading;
using System.Windows;

namespace ClaudeUsageTray
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private bool _ownsMutex;
        private TrayController? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstance = new Mutex(true, @"Local\ClaudeUsageTray", out _ownsMutex);
            if (!_ownsMutex)
            {
                // Already running — nothing to do.
                Shutdown();
                return;
            }

            _tray = new TrayController();
            _tray.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            if (_ownsMutex)
            {
                _singleInstance?.ReleaseMutex();
            }
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
