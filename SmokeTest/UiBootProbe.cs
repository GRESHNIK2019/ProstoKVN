using System;
using System.Threading;
using System.Windows;
using ProstoKVN;

public static class UiBootProbe
{
    public static void Run()
    {
        var th = new Thread(() => {
            try {
                Console.WriteLine("BOOT: constructing window");
                var w = new MainWindow { Left = -1900, Top = -1900, ShowInTaskbar = false, ShowActivated = false };
                Console.WriteLine("BOOT: window constructed");
                w.Show();
                Console.WriteLine("BOOT: window shown");
                w.UpdateLayout();
                Console.WriteLine("BOOT: onboarding measured");
                typeof(MainWindow).GetMethod("HideOnboarding",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(w,null);
                Console.WriteLine("BOOT: main navigation visible");
                w.UpdateLayout();
                Console.WriteLine("BOOT: main navigation measured");
                var tabs=(System.Windows.Controls.TabControl)w.FindName("MainTabs");
                foreach(int index in new[]{1,2,3,4,0}) { tabs.SelectedIndex=index; w.UpdateLayout(); Console.WriteLine("BOOT: tab "+index+" measured"); }
                var mode = (System.Windows.Controls.ComboBox)w.FindName("ModeBox");
                mode.IsDropDownOpen = true; w.UpdateLayout();
                Console.WriteLine("BOOT: dropdown opened");
                mode.IsDropDownOpen = false;
                var dialog = new ClientDialog(w,"Тест сообщения без изменения настроек.","Оформление диалога",MessageBoxButton.YesNo,MessageBoxImage.Warning);
                dialog.Show(); dialog.UpdateLayout();
                if (dialog.WindowStyle != WindowStyle.None || !dialog.AllowsTransparency) throw new Exception("Dialog theme mismatch");
                dialog.Close(); Console.WriteLine("BOOT: dialog measured");
                w.Close();
                Console.WriteLine("BOOT: window closed");
            } catch (Exception ex) { Console.WriteLine("BOOT: exception " + ex); Environment.ExitCode=5; }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        if (!th.Join(TimeSpan.FromSeconds(12))) {
            Console.WriteLine("BOOT_HANG");
            Environment.ExitCode = 3;
        } else Console.WriteLine("BOOT_FINISHED");
    }
}