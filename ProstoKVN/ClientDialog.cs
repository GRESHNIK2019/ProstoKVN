using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProstoKVN;

/// <summary>A bounded, owner-bound dialog with selectable, scrollable messages.</summary>
public sealed class ClientDialog : Window
{
    private MessageBoxResult result;
    public MessageBoxResult Result => result;
    public ClientDialog(Window owner,string message,string title,MessageBoxButton buttons,
        MessageBoxImage image,MessageBoxResult defaultResult=MessageBoxResult.None)
    {
        Owner=owner;Title=title;Icon=owner.Icon;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;SizeToContent=SizeToContent.Height;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;
        var area=SystemParameters.WorkArea;
        Width=Math.Min(550,Math.Max(340,area.Width-40));MaxHeight=Math.Max(280,area.Height-40);
        FontFamily=new FontFamily("Segoe UI Variable");FontSize=14;UseLayoutRounding=true;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/ProstoKVN;component/Themes/PaletteLight.xaml")});
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/ProstoKVN;component/Themes/StudioTheme.xaml")});
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/ProstoKVN;component/Themes/LayoutTheme.xaml")});
        ThemeManager.Apply(Resources,ThemeManager.IsDark(owner.Resources)?ThemeManager.Dark:ThemeManager.Light);
        Background=Brushes.Transparent;SetResourceReference(ForegroundProperty,"TextPrimary");
        result=buttons==MessageBoxButton.YesNo?MessageBoxResult.No:buttons==MessageBoxButton.OK?MessageBoxResult.OK:MessageBoxResult.Cancel;
        if(defaultResult==MessageBoxResult.None)defaultResult=result;
        var panel=new Grid{Margin=new Thickness(24,22,24,22)};
        panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new TextBlock{Text=title,FontSize=21,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)};
        heading.SetResourceReference(TextBlock.ForegroundProperty,image==MessageBoxImage.Error?"DangerInk":"TextPrimary");panel.Children.Add(heading);
        heading.Cursor=Cursors.SizeAll;
        heading.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed)DragMove();};
        var body=new TextBox{Text=message,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Background=Brushes.Transparent,
            Padding=new Thickness(0),MinHeight=0,MaxHeight=Math.Max(100,Math.Min(380,area.Height-250)),VerticalContentAlignment=VerticalAlignment.Top,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new Thickness(0,0,0,22)};
        body.SetResourceReference(ForegroundProperty,"TextPrimary");Grid.SetRow(body,1);panel.Children.Add(body);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};Grid.SetRow(actions,2);panel.Children.Add(actions);
        void Add(string text,MessageBoxResult answer,bool primary)
        {
            var button=new Button{Content=text,MinWidth=104,Margin=new Thickness(8,4,0,0),IsDefault=answer==defaultResult};
            if(!primary)button.Style=(Style)FindResource("TertiaryButton");
            button.Click+=(_,_)=>{result=answer;Close();};actions.Children.Add(button);
        }
        if(buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
        {Add("Да",MessageBoxResult.Yes,true);Add("Нет",MessageBoxResult.No,false);if(buttons==MessageBoxButton.YesNoCancel)Add("Отмена",MessageBoxResult.Cancel,false);}
        else{Add("Понятно",MessageBoxResult.OK,true);if(buttons==MessageBoxButton.OKCancel)Add("Отмена",MessageBoxResult.Cancel,false);}
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        var frame=new Border { Child=panel,CornerRadius=new CornerRadius(13),
            BorderThickness=new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty,"Surface");
        frame.SetResourceReference(Border.BorderBrushProperty,"Line");
        Content=frame;
    }
    public static MessageBoxResult Show(Window owner,string message,string title,MessageBoxButton buttons,
        MessageBoxImage image,MessageBoxResult defaultResult=MessageBoxResult.None)
    {
        var dialog=new ClientDialog(owner,message,title,buttons,image,defaultResult);
        dialog.ShowDialog();return dialog.Result;
    }
}
