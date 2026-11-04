using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ProstoKVN;

public partial class App : Application
{
    private Mutex? singleInstance;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Multiple UI instances would compete for Mihomo listeners, TUN and
        // the system proxy. Never allow a second instance to touch the tunnel.
        singleInstance = new Mutex(initiallyOwned: true,
            name: @"Local\ProstoKVN.SingleInstance", createdNew: out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("ProstoKVN уже запущен. Открой существующее окно из трея.",
                "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        // Recover a proxy left by a previous crash, but never overwrite a proxy
        // subsequently configured by another application.
        try { SystemProxyRecovery.RecoverIfStale(VpnCore.MainPort); }
        catch (Exception) { /* Snapshot stays available for a later safe retry. */ }
        base.OnStartup(e);
        _ = Task.Run(GitHubUpdateService.CleanupOldStages);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
        {
            try { singleInstance?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
