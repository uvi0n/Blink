using System;
using System.IO;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;

internal sealed class Preferences {
    static string ConfigPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Blink","preferences.xml");}}
    public string Folder=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public int Hours=12;
    public bool ConfirmClose=true,Tray;
    public string Theme="Терминал";
    public string[] Colors=(string[])Themes.Defaults.Clone();
    internal static readonly string[] Accents={"Персик","Мята","Лаванда","Голубой"};
    public static Preferences Load(){
        string legacy=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Perelet","preferences.xml");
        return Load(File.Exists(ConfigPath)?ConfigPath:legacy);
    }
    internal static Preferences Load(string location){
        var p=new Preferences();
        try{
            if(File.Exists(location)){
                var x=XElement.Load(location);string folder=(string)x.Element("folder");
                if(Directory.Exists(folder))p.Folder=folder;
                int hours;if(int.TryParse((string)x.Element("hours"),out hours)&&Array.IndexOf(new[]{1,6,12,24},hours)>=0)p.Hours=hours;
                string accent=(string)x.Element("accent");int legacy=Array.IndexOf(Accents,accent);if(legacy>=0)p.Colors[1]=new[]{"#FFAE79","#82DCC4","#C5AEFA","#91C8FF"}[legacy];
                string theme=(string)x.Element("theme");if(theme=="Перелёт")theme="Blink";if(Array.IndexOf(Themes.Names,theme)>=0)p.Theme=theme;
                foreach(var el in x.Elements("color")){int i=Array.IndexOf(Themes.Names,(string)el.Attribute("theme")=="Перелёт"?"Blink":(string)el.Attribute("theme"));if(i>=0&&Themes.Valid(el.Value))p.Colors[i]=el.Value;}
                bool tray;if(bool.TryParse((string)x.Element("tray"),out tray))p.Tray=tray;
                bool confirm;if(bool.TryParse((string)x.Element("confirmClose"),out confirm))p.ConfirmClose=confirm;
            }
        }catch(IOException){}catch(System.Xml.XmlException){}catch(UnauthorizedAccessException){}
        if(!Directory.Exists(p.Folder))p.Folder=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return p;
    }
    internal void Save(){Save(ConfigPath);}
    internal void Save(string target){
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        string temporary=target+"."+Guid.NewGuid()+".tmp";
        try{
            new XElement("preferences",new XElement("theme",Theme),System.Linq.Enumerable.Select(Themes.Names,(name,i)=>new XElement("color",new XAttribute("theme",name),Colors[i])),new XElement("tray",Tray),new XElement("folder",Folder),new XElement("hours",Hours),new XElement("confirmClose",ConfirmClose)).Save(temporary);
            if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public FrameworkElement Page(Window owner,Action<bool> close){
        FrameworkElement w;using(var s=typeof(Preferences).Assembly.GetManifestResourceStream("Settings.xaml"))w=(FrameworkElement)XamlReader.Load(s);
        var themes=(ComboBox)w.FindName("ThemeChoice");themes.ItemsSource=Themes.Names;themes.SelectedItem=Theme;
        var colors=(string[])Colors.Clone();var rgb=(Button)w.FindName("Rgb");
        Action refresh=delegate{string hex=colors[themes.SelectedIndex];rgb.Content="RGB-палитра   "+hex;rgb.Background=Themes.Brush(hex);rgb.Foreground=Themes.Brush(Themes.Ink(hex));};
        themes.SelectionChanged+=delegate{refresh();};refresh();
        rgb.Click+=delegate{using(var dlg=new System.Windows.Forms.ColorDialog{FullOpen=true,AnyColor=true,Color=System.Drawing.ColorTranslator.FromHtml(colors[themes.SelectedIndex])}){
            if(dlg.ShowDialog(new DialogOwner(new WindowInteropHelper(owner).Handle))==System.Windows.Forms.DialogResult.OK){var c=dlg.Color;colors[themes.SelectedIndex]=String.Format("#{0:X2}{1:X2}{2:X2}",c.R,c.G,c.B);refresh();}
        }};
        ((Button)w.FindName("ResetColor")).Click+=delegate{colors[themes.SelectedIndex]=Themes.Defaults[themes.SelectedIndex];refresh();};
        var tray=(CheckBox)w.FindName("Tray");tray.IsChecked=Tray;
        ((Button)w.FindName("Cancel")).Click+=delegate{close(false);};
        var folder=(TextBlock)w.FindName("Folder");folder.Text=Folder;
        var hours=(ComboBox)w.FindName("Hours");hours.ItemsSource=new[]{1,6,12,24};hours.SelectedItem=Hours;
        var confirm=(CheckBox)w.FindName("Confirm");confirm.IsChecked=ConfirmClose;
        var error=(TextBlock)w.FindName("Error");
        ((Button)w.FindName("Browse")).Click+=delegate{try{var chosen=FolderPicker.Choose(new WindowInteropHelper(owner).Handle,folder.Text);if(chosen!=null)folder.Text=chosen;}catch(Exception ex){error.Text=ex.Message;}};
        ((Button)w.FindName("Save")).Click+=delegate{
            var next=new Preferences{Tray=tray.IsChecked==true,Theme=(string)themes.SelectedItem,Colors=colors,Folder=folder.Text,Hours=(int)hours.SelectedItem,ConfirmClose=confirm.IsChecked==true};
            try{next.Save();Folder=next.Folder;Hours=next.Hours;ConfirmClose=next.ConfirmClose;Theme=next.Theme;Colors=next.Colors;Tray=next.Tray;close(true);}catch(Exception ex){error.Text="Не удалось сохранить: "+ex.Message;}
        };
        return w;
    }
}


internal sealed class DialogOwner : System.Windows.Forms.IWin32Window { public IntPtr Handle {get;private set;} public DialogOwner(IntPtr handle){Handle=handle;} }
