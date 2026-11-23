using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.RegularExpressions;

internal static class Themes {
    internal static readonly string[] Names={"Полярная ночь","Перелёт","Атлас","Плакат","Терминал","Галька"};
    internal static readonly string[] Defaults={"#93CEFF","#FFAE79","#C6DF63","#FFCF38","#A9ED98","#C7A9ED"};
    // background, card, secondary surface, text, muted, hero
    static readonly string[][] palettes={
        new[]{"#101726","#1A263B","#2C405C","#F2F6FF","#B8C9E1","#233954"},
        new[]{"#10161C","#1B242C","#28333C","#EFF4F5","#C0CFD9","#344351"},
        new[]{"#F2EEE4","#FFFCF4","#E4E1D4","#25281E","#535B47","#E4E6D9"},
        new[]{"#FFCF38","#FFFAEC","#F4DF9F","#191816","#494232","#FF8D68"},
        new[]{"#101A15","#18251C","#26382A","#E6F2E5","#A5BBA8","#203225"},
        new[]{"#F3EFF9","#FFFFFF","#E6DBF2","#392B48","#62526D","#EADFF6"}
    };
    public static bool Valid(string value){return value!=null && Regex.IsMatch(value,@"\A#[0-9A-Fa-f]{6}\z");}
    public static SolidColorBrush Brush(string hex){return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));}
    static double Linear(double b){b/=255;return b<=0.04045?b/12.92:Math.Pow((b+.055)/1.055,2.4);}
    public static string Ink(string hex){Color c=Brush(hex).Color;return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B)>.179?"#101010":"#FFFFFF";}
    public static void Apply(Window w,Preferences p){
        int i=Array.IndexOf(Names,p.Theme);
        string[] keys={"Base","Card","Surface","Text","Muted","HeroBrush"};
        for(int j=0;j<keys.Length;j++)w.Resources[keys[j]]=Brush(palettes[i][j]);
        string accent=p.Colors[i];w.Resources["Accent"]=Brush(accent);w.Resources["OnAccent"]=Brush(Ink(accent));
        int radius=new[]{14,14,3,0,3,24}[i];w.Resources["Round"]=new CornerRadius(radius);
        w.FontFamily=new FontFamily(i==4?"Consolas":"Segoe UI");w.FontSize=i==4?14:15;
        var hero=(TextBlock)w.FindName("Hero");hero.FontFamily=new FontFamily(i==2?"Georgia":i==3?"Arial Black":w.FontFamily.Source);
        hero.FontSize=new[]{34,34,43,40,30,37}[i];hero.FontWeight=i==3?FontWeights.Black:FontWeights.SemiBold;
        var header=(StackPanel)w.FindName("Navigation");
        bool side=i==0&&(w.ActualWidth>0?w.ActualWidth:w.Width)>=760;
        DockPanel.SetDock(header,side?Dock.Left:Dock.Top);header.Orientation=side?Orientation.Vertical:Orientation.Horizontal;
        header.Margin=side?new Thickness(0,4,24,0):new Thickness(0,0,0,16);
        var nav=(WrapPanel)w.FindName("Tabs");nav.Orientation=side?Orientation.Vertical:Orientation.Horizontal;nav.Margin=side?new Thickness(0,22,0,0):new Thickness(20,0,0,0);
        var grid=(Grid)w.FindName("Workspace");var selection=(Border)w.FindName("SelectionCard");var link=(Border)w.FindName("LinkCard");
        bool split=(i==2||i==3||i==4) && (w.ActualWidth>0?w.ActualWidth:w.Width)>=800;
        grid.ColumnDefinitions[1].Width=new GridLength(split?.78:0,GridUnitType.Star);
        Grid.SetColumn(link,split?1:0);Grid.SetRow(link,split?1:2);
        selection.Margin=new Thickness(0,0,split?12:0,12);
        var queue=(ListBox)w.FindName("Queue");
        var panel=new FrameworkElementFactory(i==5?typeof(WrapPanel):typeof(StackPanel));
        queue.ItemsPanel=new ItemsPanelTemplate(panel);
        queue.MaxHeight=i==4?220:170;
        var artwork=(FrameworkElement)w.FindName("Artwork");artwork.Visibility=i==4||i==2?Visibility.Collapsed:Visibility.Visible;
        ((StackPanel)w.FindName("HeroWords")).Margin=new Thickness(0,0,artwork.Visibility==Visibility.Visible?123:0,0);
    }
}

