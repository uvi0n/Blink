using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

public class Entry {
    public string Path{get;set;} public string Name{get{return System.IO.Path.GetFileName(Path);}}
    public string Size{get;set;}
}
class Desk {
    readonly Window window;readonly System.Windows.Forms.NotifyIcon trayIcon;bool exitTray;
    readonly Preferences preferences=Preferences.Load();
    readonly ObservableCollection<Entry> files=new ObservableCollection<Entry>();
    readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
    readonly string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    string pendingFolder; string folder;bool receive;Session session;Link link;
    [STAThread] static void Main(string[] args){
        if(args.Length==1 && args[0]=="--install-sendto"){
            try{dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shortcut=shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo),"Blink.lnk"));
            shortcut.TargetPath=typeof(Desk).Assembly.Location;shortcut.WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory;shortcut.Save();MessageBox.Show("Blink добавлен в меню «Отправить».","Blink");}catch(Exception ex){MessageBox.Show(ex.Message,"Blink");}return;}
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        var app=new Application();var desk=new Desk();desk.Add(args);app.Run(desk.window);
    }
    T Find<T>(string name) where T:FrameworkElement{return (T)window.FindName(name);}
    Button B(string n){return Find<Button>(n);}TextBlock T(string n){return Find<TextBlock>(n);}
    ListBox Queue{get{return Find<ListBox>("Queue");}} TextBox Address{get{return Find<TextBox>("Address");}}
    static Visibility Show(bool visible){return visible?Visibility.Visible:Visibility.Collapsed;}
    internal Desk(){
        using(var stream=typeof(Desk).Assembly.GetManifestResourceStream("Desk.xaml"))window=(Window)XamlReader.Load(stream);
        window.Width=Math.Min(window.Width,SystemParameters.WorkArea.Width);window.Height=Math.Min(window.Height,SystemParameters.WorkArea.Height);
        folder=preferences.Folder;
        Extras.Style(window);
        B("Backup").Click+=delegate{Backup();};
        B("FolderAdd").Click+=delegate{var path=FolderPicker.Choose(new WindowInteropHelper(window).Handle,folder);if(path!=null)Prepare(path,false);};
        B("Encrypt").Click+=delegate{var d=new OpenFileDialog();if(d.ShowDialog(window)==true)Prepare(d.FileName,true);};
        B("Unlock").Click+=delegate{try{Extras.Decrypt(window);}catch(Exception e){T("State").Text=e.Message;}};
        ApplyAccent();window.SizeChanged+=delegate{ApplyAccent();};
        trayIcon=new System.Windows.Forms.NotifyIcon{Text="Blink",Icon=System.Drawing.Icon.ExtractAssociatedIcon(typeof(Desk).Assembly.Location)};
        var menu=new System.Windows.Forms.ContextMenuStrip();menu.Items.Add("Открыть",null,delegate{Restore();});menu.Items.Add("Выйти",null,delegate{exitTray=true;Restore();window.Close();exitTray=false;});trayIcon.ContextMenuStrip=menu;trayIcon.DoubleClick+=delegate{Restore();};
        B("Settings").Click+=delegate{
            var host=Find<ContentControl>("SettingsHost");if(host.Content!=null){HideSettings();return;}
            string before=preferences.Folder;
            host.Content=preferences.Page(window,saved=>{if(saved){ApplyAccent();if(before!=preferences.Folder){if(session==null)folder=preferences.Folder;else pendingFolder=preferences.Folder;}}HideSettings();Sync();});
            host.Visibility=Visibility.Visible;Find<ScrollViewer>("TransferPage").Visibility=Visibility.Collapsed;
        };
        B("QrButton").Click+=delegate{var qr=Find<Image>("Qr");if(qr.Visibility==Visibility.Visible){qr.Visibility=Visibility.Collapsed;return;}if(Address.Text.Length>0){qr.Source=LinkQr.Render(Address.Text);qr.Visibility=Visibility.Visible;}};
        window.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){
            if(preparing){e.Cancel=true;T("State").Text="Дождитесь завершения подготовки файла.";return;}
            if(preferences.Tray&&!exitTray){e.Cancel=true;trayIcon.Visible=true;window.Hide();return;}
            if(session!=null && preferences.ConfirmClose)e.Cancel=MessageBox.Show(window,"Закрыть приложение и отозвать действующую ссылку?","Blink",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes;
        };
        Queue.ItemsSource=files;Queue.SelectionChanged+=delegate{Sync();};
        B("SendTab").Click+=delegate{SetMode(false);};B("ReceiveTab").Click+=delegate{SetMode(true);};
        B("Add").Click+=delegate{var d=new OpenFileDialog{Multiselect=true,Title="Добавить файлы"};if(d.ShowDialog(window)==true)Add(d.FileNames);};
        B("Remove").Click+=delegate{foreach(var entry in Queue.SelectedItems.Cast<Entry>().ToArray())files.Remove(entry);Sync();};
        B("Desktop").Click+=delegate{if(Directory.Exists(desktop)){folder=desktop;Sync();}else T("State").Text="Windows не вернула доступную папку рабочего стола. Выберите папку вручную.";};
        B("Browse").Click+=delegate{try{var choice=FolderPicker.Choose(new WindowInteropHelper(window).Handle,folder);if(choice!=null){folder=choice;Sync();}}catch(Exception ex){T("State").Text=ex.Message;}};
        B("Create").Click+=delegate{Start();};B("Stop").Click+=delegate{End();T("State").Text="Ссылка отозвана.";};
        B("Copy").Click+=delegate{try{Clipboard.SetText(Address.Text);T("State").Text="Ссылка скопирована.";}catch{Address.Focus();Address.SelectAll();}};
        window.PreviewDragOver+=delegate(object sender,DragEventArgs e){e.Effects=session==null&&!receive&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        window.Drop+=delegate(object sender,DragEventArgs e){if(!receive&&session==null&&e.Data.GetDataPresent(DataFormats.FileDrop))Add((string[])e.Data.GetData(DataFormats.FileDrop));};
        window.Closed+=delegate{timer.Stop();trayIcon.Dispose();End();foreach(var file in scratch)try{File.Delete(file);Directory.Delete(Path.GetDirectoryName(file));}catch(IOException){}};
        timer.Tick+=delegate{if(session==null)return;T("Outgoing").Text=session.Outgoing;if(DateTime.UtcNow>=session.Expires){End();T("State").Text="Срок ссылки истёк.";}else if(session.Status!=null)T("State").Text=session.Status;};
        timer.Start();SetMode(false);
    }
    Link backup;string backupUrl;bool preparing;
    readonly System.Collections.Generic.List<string> scratch=new System.Collections.Generic.List<string>();
    async void Backup(){
        if(session==null)return;if(backupUrl!=null){Clipboard.SetText(backupUrl);T("State").Text="Запасная ссылка скопирована.";return;}if(backup!=null)return;
        var other=backup=new Link(session,link.Provider=="localhost.run"?"Cloudflare":"localhost.run");
        try{await other.Run((url,msg)=>{if(backup!=other)return;backupUrl=url;T("State").Text=url==null?msg:"Запасной адрес проверен. Нажмите кнопку ещё раз для копирования.";});}
        catch(OperationCanceledException){}catch(Exception e){if(backup==other){backup=null;backupUrl=null;T("State").Text=e.Message;}}finally{other.Dispose();}
    }
    async void Prepare(string path,bool encrypt){
        if(preparing||session!=null)return;if(encrypt&&files.Count>0&&MessageBox.Show(window,"Заменить подборку только зашифрованным файлом?","Blink",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;preparing=true;Sync();
        try{string result=await System.Threading.Tasks.Task.Run(()=>Extras.Prepare(path,encrypt));scratch.Add(result);if(encrypt)files.Clear();Add(new[]{result});T("State").Text="Добавлено: "+Path.GetFileName(result);}
        catch(Exception e){T("State").Text=e.Message;}finally{preparing=false;Sync();}
    }
    void Restore(){window.Show();window.WindowState=WindowState.Normal;window.Activate();trayIcon.Visible=false;}
    void ApplyAccent(){Themes.Apply(window,preferences);RefreshTabs();}
    void RefreshTabs(){B("SendTab").Background=receive?Brushes.Transparent:(Brush)window.Resources["Surface"];B("ReceiveTab").Background=!receive?Brushes.Transparent:(Brush)window.Resources["Surface"];}
    void HideSettings(){var host=Find<ContentControl>("SettingsHost");host.Content=null;host.Visibility=Visibility.Collapsed;Find<ScrollViewer>("TransferPage").Visibility=Visibility.Visible;}
    async void Add(string[] paths){foreach(string path in paths){if(Directory.Exists(path)){
            if(session!=null||preparing)continue;preparing=true;Sync();
            try{string packed=await System.Threading.Tasks.Task.Run(()=>Extras.Prepare(path,false));scratch.Add(packed);files.Add(new Entry{Path=packed,Size=Session.SizeText(new FileInfo(packed).Length)});}
            catch(Exception e){T("State").Text=e.Message;}finally{preparing=false;Sync();}continue;
        }if(!File.Exists(path)||files.Any(x=>String.Equals(x.Path,path,StringComparison.OrdinalIgnoreCase)))continue;
        try{files.Add(new Entry{Path=path,Size=Session.SizeText(new FileInfo(path).Length)});}catch(IOException ex){T("State").Text=ex.Message;}}
        Sync();
    }
    void SetMode(bool incoming){
        HideSettings();receive=incoming;Find<CheckBox>("Approve").Visibility=Show(!incoming);Find<StackPanel>("SendPane").Visibility=Show(!incoming);Find<StackPanel>("ReceivePane").Visibility=Show(incoming);
        RefreshTabs();
        T("Hero").Text=incoming?"Запросить файл":"Отправить файлы";
        T("Hint").Text=incoming?"Получатель ссылки сможет отправить один файл на ваш компьютер.":"Соберите подборку и отправьте её одной ссылкой.";
        T("State").Text="Ссылка появится после проверки соединения.";Sync();
    }
    void Sync(){bool idle=session==null&&!preparing;foreach(string id in new[]{"FolderAdd","Encrypt","Unlock"})B(id).IsEnabled=idle;Find<CheckBox>("Approve").IsEnabled=Find<CheckBox>("Collect").IsEnabled=idle;B("Backup").Visibility=Show(session!=null);
        foreach(var n in new[]{"Add","SendTab","ReceiveTab","Browse","Desktop"})B(n).IsEnabled=idle;
        B("Create").IsEnabled=idle&&(receive?Directory.Exists(folder):files.Count>0);
        B("Remove").IsEnabled=idle&&Queue.SelectedItems.Count>0;B("Remove").Visibility=Show(files.Count>0);
        T("Section").Text=receive?"Папка для получения":files.Count==0?"Подборка":"Подборка · "+files.Count;
        T("FolderPath").Text=folder;T("DropHint").Visibility=Show(files.Count==0);
        B("QrButton").Visibility=Address.Visibility=B("Copy").Visibility=Show(Address.Text.Length>0);B("Stop").Visibility=Show(!idle);
    }
    async void Start(){
        if(session!=null||preparing)return;Link current=null;
        try{if(receive&&!Directory.Exists(folder))throw new IOException("Выбранная папка недоступна.");
            session=new Session(receive?new[]{folder}:files.Select(x=>x.Path).ToArray(),receive,preferences.Hours);
            session.Collect=receive&&Find<CheckBox>("Collect").IsChecked==true;
            if(!receive&&Find<CheckBox>("Approve").IsChecked==true)session.Authorize=code=>window.Dispatcher.Invoke(()=>MessageBox.Show(window,"Код получателя: "+code+". Разрешить доступ?","Blink",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes);
            current=link=new Link(session);Sync();
            await current.Run((url,message)=>{if(link==current){if(Address.Text!=(url??"")){var qr=Find<Image>("Qr");qr.Source=null;qr.Visibility=Visibility.Collapsed;}Address.Text=url??"";T("State").Text=message;Sync();}});
        }catch(OperationCanceledException){}catch(Exception e){if(current==link){if(backup==null)End();else{Address.Clear();Find<Image>("Qr").Visibility=Visibility.Collapsed;Sync();}T("State").Text=backup==null?e.Message:"Основной туннель закрылся. Используйте запасной адрес."; }}
    }
    void End(){var spare=backup;backup=null;backupUrl=null;if(spare!=null)spare.Dispose();var old=link;if(session!=null)T("Outgoing").Text=receive?session.Status:session.Outgoing;link=null;if(old!=null)old.Dispose();if(session!=null)session.Dispose();session=null;if(pendingFolder!=null){folder=pendingFolder;pendingFolder=null;}Address.Clear();if(old!=null && T("Outgoing").Text.Length>0)T("Outgoing").Text="Итог передачи\n"+T("Outgoing").Text;Find<Image>("Qr").Source=null;Find<Image>("Qr").Visibility=Visibility.Collapsed;Sync();}
}






