using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace DlutTimetable.Windows;

public static class WindowFrame
{
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Content is not UIElement content) return;

        var iconPath=Path.Combine(AppContext.BaseDirectory,"Assets","DlutTimetable.ico");
        if(File.Exists(iconPath))
        {
            using var iconStream=File.OpenRead(iconPath);
            window.Icon=new IconBitmapDecoder(iconStream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        }

        window.Content = null;
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window,new WindowChrome
        {
            CaptionHeight=0,
            CornerRadius=new(10),
            GlassFrameThickness=new Thickness(0),
            ResizeBorderThickness=new Thickness(6),
            UseAeroCaptionButtons=false
        });

        var header=new SolidColorBrush(Color.FromRgb(226,233,243));header.Freeze();
        var frame=new SolidColorBrush(Color.FromRgb(155,183,216));frame.Freeze();
        var root=new Grid();
        var border=new Border{Background=Brushes.White,BorderBrush=frame,BorderThickness=new(1),CornerRadius=new(10),ClipToBounds=true};
        var contentGrid=new Grid();contentGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});contentGrid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var titleBar=new Grid{Height=40,Background=header};
        titleBar.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        titleBar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var title=new TextBlock{Text=window.Title,FontFamily=new("Segoe UI, Microsoft YaHei UI"),FontSize=13,FontWeight=FontWeights.Medium,
            Foreground=new SolidColorBrush(Color.FromRgb(41,58,78)),VerticalAlignment=VerticalAlignment.Center,Margin=new(14,0,8,0)};
        titleBar.Children.Add(title);
        WindowChrome.SetIsHitTestVisibleInChrome(title,true);
        var chromeButtons=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Stretch};
        Grid.SetColumn(chromeButtons,1);titleBar.Children.Add(chromeButtons);
        chromeButtons.Children.Add(Button("—","最小化",(_,_)=>SystemCommands.MinimizeWindow(window)));
        chromeButtons.Children.Add(Button("□","最大化或还原",(_,_)=>
        {
            if(window.WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(window);
            else SystemCommands.MaximizeWindow(window);
        }));
        chromeButtons.Children.Add(Button("×","关闭",(_,_)=>SystemCommands.CloseWindow(window),close:true));
        titleBar.MouseLeftButtonDown+=(_,e)=>
        {
            if(e.ClickCount==2)
            {
                if(window.WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            }
            else if(e.ButtonState==System.Windows.Input.MouseButtonState.Pressed)window.DragMove();
        };
        Grid.SetRow(titleBar,0);contentGrid.Children.Add(titleBar);
        Grid.SetRow(content,1);contentGrid.Children.Add(content);
        border.Child=contentGrid;root.Children.Add(border);
        window.Content=root;

        Button Button(string glyph,string help,RoutedEventHandler click,bool close=false)
        {
            var button=new Button{Content=glyph,Width=44,Padding=new(0),BorderThickness=new(0),Background=Brushes.Transparent,
                Foreground=new SolidColorBrush(close?Color.FromRgb(78,61,67):Color.FromRgb(55,69,88)),FontFamily=new("Segoe UI"),FontSize=14,
                ToolTip=help,Focusable=false};
            WindowChrome.SetIsHitTestVisibleInChrome(button,true);
            button.Click+=click;
            button.MouseEnter+=(_,_)=>button.Background=new SolidColorBrush(Color.FromArgb(close?(byte)225:(byte)38,close?(byte)215:(byte)90,close?(byte)72:(byte)130,close?(byte)78:(byte)176));
            button.MouseLeave+=(_,_)=>button.Background=Brushes.Transparent;
            return button;
        }
    }
}
