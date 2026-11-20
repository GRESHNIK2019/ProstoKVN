using System.Windows;
using Forms = System.Windows.Forms;

namespace ProstoKVN;

public partial class MainWindow
{
    private Forms.NotifyIcon? trayIcon;
    private System.Drawing.Icon? trayImage;
    private bool exitRequested;
    // Isolated smoke-test windows continue to support programmatic Close().
    private bool closeToTrayEnabled = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME"));
    private bool trayHintShown;
    private WindowState windowStateBeforeTray = WindowState.Normal;

    private void InitializeTray()
    {
        if (trayIcon != null) return;
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/ProstoKVN;component/Assets/ProstoKVN.ico"));
        using var stream = resource!.Stream;
        trayImage = new System.Drawing.Icon(stream);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть ProstoKVN", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => RequestExit());
        trayIcon = new Forms.NotifyIcon { Icon = trayImage, Text = "ProstoKVN", ContextMenuStrip = menu, Visible = true };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        UpdateTrayStatus();
    }

    private void HideToTray()
    {
        if (isWindowClosing) return;
        InitializeTray();
        windowStateBeforeTray = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        Hide();
        ShowInTaskbar = false;
        if (!trayHintShown && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")))
            trayIcon!.ShowBalloonTip(2500, "ProstoKVN работает в фоне",
                "Откройте окно двойным щелчком по значку. Для завершения выберите «Выход» в меню правой кнопки мыши.", Forms.ToolTipIcon.Info);
        trayHintShown = true;
    }

    private void RestoreFromTray()
    {
        if (isWindowClosing) return;
        ShowInTaskbar = true;
        Show();
        WindowState = windowStateBeforeTray;
        Activate();
    }

    private void RequestExit()
    {
        if (isWindowClosing) return;
        exitRequested = true;
        Close();
    }

    internal void PrepareForSessionEnd() => exitRequested = true;

    private void UpdateTrayStatus()
    {
        if (trayIcon == null) return;
        string text = "ProstoKVN — " + ConnectionTitle.Text;
        trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void DisposeTray()
    {
        if (trayIcon == null) return;
        trayIcon.Visible = false;
        trayIcon.ContextMenuStrip?.Dispose();
        trayIcon.Dispose();
        trayIcon = null;
        trayImage?.Dispose();
        trayImage = null;
    }
}
