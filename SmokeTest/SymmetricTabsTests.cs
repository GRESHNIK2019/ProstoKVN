using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProstoKVN;

public static class SymmetricTabsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("SYMMETRIC_TABS_FAILED: "+label);
        Console.WriteLine("PASS "+label);
    }
    static string GetBrush(Border tab)
        => (tab.Background as SolidColorBrush)?.Color.ToString() ?? "";

    static Border Surface(TabItem item)
    {
        item.ApplyTemplate();
        return (Border)item.Template.FindName("TabSurface",item);
    }

    static void CheckTabs(MainWindow window, string variant)
    {
        window.UpdateLayout();
        var tabs=(TabControl)window.FindName("MainTabs");
        var items=tabs.Items.Cast<TabItem>().ToArray();
        Check(items.Length==4, variant+": four tabs preserved");
        var widths=items.Select(x=>x.ActualWidth).ToArray();
        var bounds=items.Select(x=>x.TransformToAncestor(tabs)
            .Transform(new Point(0,0)).X).ToArray();
        Check(widths.All(w=>w>130) && widths.Max()-widths.Min()<1,
            variant+": all tab allocations exactly equal");
        Check(bounds[0]<bounds[1] && bounds[1]<bounds[2] && bounds[2]<bounds[3],
            variant+": tabs aligned left to right");
        for(int i=0;i<4;i++)
        {
            tabs.SelectedIndex=i;
            window.UpdateLayout();
            string active=GetBrush(Surface(items[i]));
            Check(items[i].IsSelected && items.Where((x,j)=>j!=i).All(x=>!x.IsSelected),
                variant+": selection at tab "+i);
            Check(items.Where((x,j)=>j!=i).All(x=>GetBrush(Surface(x))!=active),
                variant+": selected surface visibly different at "+i);
        }
        tabs.SelectedIndex=0;
    }
    static void Preview(MainWindow window,string variant)
    {
        string? folder=Environment.GetEnvironmentVariable("PROSTOKVN_SYMMETRIC_PREVIEW");
        if(string.IsNullOrWhiteSpace(folder)) return;
        window.UpdateLayout();
        string path=Path.Combine(folder,"symmetric-tabs-"+variant+".png");
        Directory.CreateDirectory(folder);
        var bmp=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        bmp.Render(window);
        var writer=new PngBitmapEncoder();
        writer.Frames.Add(BitmapFrame.Create(bmp));
        using var file=File.Create(path);
        writer.Save(file);
        Console.WriteLine("SCREENSHOT="+path);
    }
    public static void Run()
    {
        Exception? fail=null;
        var thread=new Thread(()=>
        {
            try
            {
                UserStore.EnsureHome();
                UserStore.SaveSettings(new AppSettings{Theme="dark",Mode="smart",Tun=true,
                    ProtectedUrl=UserStore.EncryptUrl("https://example.com/sub/test")});
                var window=new MainWindow{WindowStartupLocation=WindowStartupLocation.Manual,
                    Left=-1800,Top=-1800,ShowInTaskbar=false,ShowActivated=false};
                // Isolated UI tests never contact a real subscription.
                window.StartupSubscriptionLoader = _ =>
                    System.Threading.Tasks.Task.FromException<SubscriptionImporter.Result>(
                        new InvalidOperationException("Offline preview fixture"));
                window.Show();
                CheckTabs(window,"dark");
                Preview(window,"dark");
                var themeButton=(Button)window.FindName("ThemeButton");
                themeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                CheckTabs(window,"light");
                Preview(window,"light");
                var tabs=(TabControl)window.FindName("MainTabs");
                tabs.SelectedIndex=2;
                Preview(window,"light-settings");
                themeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Preview(window,"dark-settings");
                window.Close();
                Console.WriteLine("ALL_SYMMETRIC_TABS_TESTS_PASS");
            }
            catch(Exception ex){fail=ex;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Tab test timed out");
        if(fail!=null) throw new Exception("Tab layout verification failed",fail);
    }
}
