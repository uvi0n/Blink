using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

internal static class Extras {
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    internal static void Style(Window w){
        using(var s=typeof(Extras).Assembly.GetManifestResourceStream("dagger.png")){
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=s;image.EndInit();image.Freeze();((Image)w.FindName("Badge")).Source=image;((Image)w.FindName("HeroBadge")).Source=image;
        }
        w.SourceInitialized+=delegate{int dark=1,color=0x201910;var hwnd=new WindowInteropHelper(w).Handle;
            DwmSetWindowAttribute(hwnd,20,ref dark,4);DwmSetWindowAttribute(hwnd,35,ref color,4);};
    }
    static void Pack(ZipArchive zip,DirectoryInfo dir,string prefix,string staging){
        zip.CreateEntry(prefix+"/");
        foreach(var entry in dir.EnumerateFileSystemInfos()){
            if(String.Equals(entry.FullName,staging,StringComparison.OrdinalIgnoreCase))continue;
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0)continue;
            string name=prefix+"/"+entry.Name;
            if(entry is DirectoryInfo)Pack(zip,(DirectoryInfo)entry,name,staging);
            else using(var input=File.OpenRead(entry.FullName))using(var output=zip.CreateEntry(name,CompressionLevel.NoCompression).Open())input.CopyTo(output);
        }
    }
    static void Age(string args){
        string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"age.exe");
        using(var p=Process.Start(new ProcessStartInfo(exe,args){UseShellExecute=true})){p.WaitForExit();if(p.ExitCode!=0)throw new IOException("age не завершил операцию. Проверьте пароль и свободное место.");}
    }
    internal static string Prepare(string path,bool encrypt){
        string dir=Path.Combine(Path.GetTempPath(),"Blink-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string output=Path.Combine(dir,encrypt?"Private.age":new DirectoryInfo(path).Name+".zip");
        try{if(encrypt)Age("-p -o \""+output+"\" \""+path+"\"");else using(var f=new FileStream(output,FileMode.CreateNew))using(var z=new ZipArchive(f,ZipArchiveMode.Create))Pack(z,new DirectoryInfo(path),new DirectoryInfo(path).Name,dir);
            return output;
        }catch{File.Delete(output);Directory.Delete(dir);throw;}
    }
    internal static void Decrypt(Window w){
        var input=new OpenFileDialog{Filter="Файл age|*.age|Все файлы|*.*"};if(input.ShowDialog(w)!=true)return;
        var save=new SaveFileDialog{Title="Сохранить под исходным именем с расширением"};if(save.ShowDialog(w)!=true)return;
        string tmp=save.FileName+"."+Guid.NewGuid().ToString("N")+".partial";
        try{Age("-d -o \""+tmp+"\" \""+input.FileName+"\"");if(File.Exists(save.FileName))File.Replace(tmp,save.FileName,null);else File.Move(tmp,save.FileName);}finally{File.Delete(tmp);}
    }
    internal static byte[] Thumbnail(string path){
        var b=new BitmapImage();using(var stream=File.OpenRead(path)){b.BeginInit();b.StreamSource=stream;b.CacheOption=BitmapCacheOption.OnLoad;b.DecodePixelWidth=160;b.EndInit();b.Freeze();}
        var enc=new JpegBitmapEncoder{QualityLevel=75};enc.Frames.Add(BitmapFrame.Create(b));using(var m=new MemoryStream()){enc.Save(m);return m.ToArray();}
    }
}
// Byte ranges in a deterministic ZIP stream. Skipped data is read locally for CRC calculation.
internal sealed class RangeSink:Stream{
    readonly Stream dest;long skip,left; public int LastWrite {get;private set;} public long PayloadOverlap(long size){return Math.Max(0,Math.Min(left,size-skip));} public RangeSink(Stream d,long start,long count){dest=d;skip=start;left=count;}
    public override void Write(byte[] b,int o,int n){int drop=(int)Math.Min(skip,n);skip-=drop;o+=drop;n-=drop;int send=(int)Math.Min(left,n);LastWrite=send;if(send>0){dest.Write(b,o,send);left-=send;}}
    public override bool CanRead{get{return false;}} public override bool CanWrite{get{return true;}} public override bool CanSeek{get{return false;}}
    public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
    public override void Flush(){dest.Flush();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();} public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}
}


