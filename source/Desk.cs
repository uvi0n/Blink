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
    public long Bytes{get;set;}
    public string Size{get{return Session.SizeText(Bytes);}}
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
            shortcut.TargetPath=Boot.Root()==null?typeof(Desk).Assembly.Location:Path.Combine(Boot.Root(),"Blink.exe");shortcut.WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory;shortcut.Save();MessageBox.Show("Blink добавлен в меню «Отправить».","Blink");}catch(Exception ex){MessageBox.Show(ex.Message,"Blink");}return;}
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
        B("NewTransfer").Click+=delegate{try{Process.Start(typeof(Desk).Assembly.Location);}catch(Exception ex){MessageBox.Show(window,ex.Message,"Blink");}};
        B("Bug").Click+=delegate{
            var menuBug=new ContextMenu();
            var mail=new MenuItem{Header="Написать на prusenkodanila@gmail.com"};
            mail.Click+=delegate{
                string uri="mailto:prusenkodanila@gmail.com?subject="+Uri.EscapeDataString("Ошибка в Blink "+Boot.Current)+"&body="+Uri.EscapeDataString("Что произошло?\r\n\r\nКак повторить?\r\n\r\nОжидаемый результат\r\n\r\nВерсия Blink "+Boot.Current);
                try{Process.Start(new ProcessStartInfo(uri){UseShellExecute=true});}catch{MessageBox.Show(window,"Откройте свою почту и напишите на prusenkodanila@gmail.com.","Нашёл баг");}
            };
            menuBug.Items.Add(mail);menuBug.PlacementTarget=B("Bug");menuBug.IsOpen=true;
        };
        Queue.AddHandler(Button.ClickEvent,new RoutedEventHandler(delegate(object sender,RoutedEventArgs e){
            var button=e.OriginalSource as Button;
            if(button!=null && button.Tag as string=="RemoveEntry" && session==null && !preparing){
                var entry=button.DataContext as Entry;if(entry!=null)files.Remove(entry);Sync();e.Handled=true;
            }
        }));
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
        timer.Tick+=delegate{if(session==null)return;T("Outgoing").Text=session.Outgoing;RefreshLive();if(DateTime.UtcNow>=session.Expires){End();T("State").Text="Срок ссылки истёк.";}else if(session.Status!=null)T("State").Text=session.Status;};
        preferences.CheckUpdates=CheckUpdates;window.ContentRendered+=async delegate{if(preferences.AutoUpdate)await CheckUpdates();};
        window.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){
            bool settings=Find<ContentControl>("SettingsHost").Content!=null;
            if(e.Key==System.Windows.Input.Key.Escape&&settings){HideSettings();e.Handled=true;}
            if(settings)return;
            if(e.Key==System.Windows.Input.Key.O && System.Windows.Input.Keyboard.Modifiers==System.Windows.Input.ModifierKeys.Control && !receive && B("Add").IsEnabled){
                B("Add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));e.Handled=true;
            }else if(e.Key==System.Windows.Input.Key.Delete && Queue.IsKeyboardFocusWithin && B("Remove").IsEnabled){
                B("Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));e.Handled=true;
            }
        };
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
    System.Threading.Tasks.Task<string> checking;
    async System.Threading.Tasks.Task<string> CheckUpdates(){
        if(checking==null||checking.IsCompleted)checking=System.Threading.Tasks.Task.Run(()=>Updates.Check());
        try{return await checking;}catch(Exception ex){return "Обновление недоступно: "+ex.Message;}
    }
    void Restore(){window.Show();window.WindowState=WindowState.Normal;window.Activate();trayIcon.Visible=false;}
    void ApplyAccent(){Themes.Apply(window,preferences);RefreshTabs();}
    void RefreshTabs(){B("SendTab").Background=receive?Brushes.Transparent:(Brush)window.Resources["Surface"];B("ReceiveTab").Background=!receive?Brushes.Transparent:(Brush)window.Resources["Surface"];}
    void HideSettings(){var host=Find<ContentControl>("SettingsHost");host.Content=null;host.Visibility=Visibility.Collapsed;Find<ScrollViewer>("TransferPage").Visibility=Visibility.Visible;}
    async void Add(string[] paths){foreach(string path in paths){if(Directory.Exists(path)){
            if(session!=null||preparing)continue;preparing=true;Sync();
            try{string packed=await System.Threading.Tasks.Task.Run(()=>Extras.Prepare(path,false));scratch.Add(packed);files.Add(new Entry{Path=packed,Bytes=new FileInfo(packed).Length});}
            catch(Exception e){T("State").Text=e.Message;}finally{preparing=false;Sync();}continue;
        }if(!File.Exists(path)||files.Any(x=>String.Equals(x.Path,path,StringComparison.OrdinalIgnoreCase)))continue;
        try{files.Add(new Entry{Path=path,Bytes=new FileInfo(path).Length});}catch(IOException ex){T("State").Text=ex.Message;}}
        Sync();
    }
    void SetMode(bool incoming){
        HideSettings();receive=incoming;Find<CheckBox>("Approve").Visibility=Show(!incoming);Find<StackPanel>("SendPane").Visibility=Show(!incoming);Find<StackPanel>("ReceivePane").Visibility=Show(incoming);
        RefreshTabs();
        T("Hero").Text=incoming?"Запросить файл":"Отправить файлы";
        T("Hint").Text=incoming?"Получатель ссылки сможет отправить один файл на ваш компьютер.":"Соберите подборку и отправьте её одной ссылкой.";
        T("State").Text="Ссылка появится после проверки соединения.";Sync();
    }
    void Sync(){bool idle=session==null&&!preparing;Queue.IsEnabled=idle;foreach(string id in new[]{"FolderAdd","Encrypt","Unlock"})B(id).IsEnabled=idle;Find<CheckBox>("Approve").IsEnabled=Find<CheckBox>("Collect").IsEnabled=idle;B("Backup").Visibility=Show(session!=null);
        foreach(var n in new[]{"Add","SendTab","ReceiveTab","Browse","Desktop"})B(n).IsEnabled=idle;
        B("Create").IsEnabled=idle&&(receive?Directory.Exists(folder):files.Count>0);
        B("Remove").IsEnabled=idle&&Queue.SelectedItems.Count>0;B("Remove").Visibility=Show(Queue.SelectedItems.Count>0&&idle);
        T("Section").Text=receive?"Папка для получения":files.Count==0?"Подборка":"Подборка · "+files.Count;
        T("FolderPath").Text=folder;Find<StackPanel>("DropZone").Visibility=Show(files.Count==0);
        RefreshOverview();RefreshLive();
        B("QrButton").Visibility=Address.Visibility=B("Copy").Visibility=Show(Address.Text.Length>0);B("Stop").Visibility=Show(!idle);
    }
    void RefreshLive(){
        if(session==null || receive){Find<StackPanel>("LivePanel").Visibility=Visibility.Collapsed;return;}
        var snapshot=session.TransferSnapshot;
        bool show=snapshot.HasData && snapshot.Total>0;
        Find<StackPanel>("LivePanel").Visibility=Show(show);
        if(!show)return;
        double share=Math.Min(1,(double)snapshot.Sent/snapshot.Total);
        T("LiveCaption").Text=(snapshot.Active>0?"Активных скачиваний: "+snapshot.Active:"Последнее скачивание")+"   ·   "+(int)(100*share)+"%";
        ((ScaleTransform)Find<Border>("LiveFill").RenderTransform).ScaleX=share;
    }
    static int FileGroup(string path){
        switch(System.IO.Path.GetExtension(path).ToLowerInvariant()){
            case ".png":case ".jpeg":case ".jpg":case ".gif":case ".webp":case ".tif":case ".heic":case ".bmp":case ".svg":return 0;
            case ".mp4":case ".mov":case ".webm":case ".avi":case ".mkv":case ".m4v":return 1;
            case ".zip":case ".7z":case ".rar":case ".gz":case ".tar":case ".age":return 2;
            default:return 3;
        }
    }
    void RefreshOverview(){
        bool online=session!=null&&Address.Text.Length>0;
        T("SessionPhase").Text=preparing?"Подготовка файлов":session==null?"Подготовка передачи":online?"Ссылка доступна":"Проверка соединения";
        Find<System.Windows.Shapes.Ellipse>("StatusLamp").SetResourceReference(System.Windows.Shapes.Shape.FillProperty,online?"Accent":"Muted");
        T("LinkExpiry").Text=session==null?"Срок ссылки · "+preferences.Hours+" ч":"Доступ до "+session.Expires.ToLocalTime().ToString("dd.MM, HH:mm");
        long total=files.Sum(f=>f.Bytes);
        T("SizeTotal").Text=receive||files.Count==0?"":Session.SizeText(total);
        Find<StackPanel>("MixPanel").Visibility=Show(!receive&&total>0);
        var bar=Find<Grid>("MixBar");var legend=Find<WrapPanel>("MixLegend");
        bar.Children.Clear();bar.ColumnDefinitions.Clear();legend.Children.Clear();
        if(receive||total==0)return;
        var labels=new[]{"Фото","Видео","Архивы","Прочее"};
        var paints=new[]{"#56BBA8","#829BE5","#C99CB6","#A6AA93"};
        foreach(var group in files.GroupBy(f=>FileGroup(f.Path)).OrderByDescending(g=>g.Sum(f=>f.Bytes))){
            long bytes=group.Sum(f=>f.Bytes);int index=group.Key;
            string label=labels[index]+" · "+Session.SizeText(bytes);
            var brush=Themes.Brush(paints[index]);int column=bar.ColumnDefinitions.Count;
            bar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength((double)bytes/total,GridUnitType.Star)});
            var segment=new Border{Background=brush,ToolTip=label,Margin=new Thickness(0,0,1,0)};Grid.SetColumn(segment,column);bar.Children.Add(segment);
            var key=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,12,2)};
            key.Children.Add(new Border{Background=brush,Width=8,Height=8,CornerRadius=new CornerRadius(2),VerticalAlignment=VerticalAlignment.Center});
            key.Children.Add(new TextBlock{Text=label,FontSize=13,Margin=new Thickness(5,0,0,0)});legend.Children.Add(key);
        }
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









