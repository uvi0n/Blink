using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Updates {
    internal sealed class Asset { public string name{get;set;} public string digest{get;set;} public string browser_download_url{get;set;} }
    internal sealed class Release { public string tag_name{get;set;} public Asset[] assets{get;set;} public bool prerelease{get;set;} public bool draft{get;set;} }
    internal static readonly string[] Files={"Blink.exe","Blink.exe.config","QRCoder.dll","age.exe","cloudflared.exe","lhr_hosts"};
    internal static void Install(string root,string version,string zip,string digest){
        if(!Regex.IsMatch(version,@"^[0-9]+\.[0-9]+\.[0-9]+$"))throw new InvalidDataException("Некорректная версия.");
        using(var f=new FileStream(zip,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var sha=SHA256.Create()){
            if(!string.Equals("sha256:"+BitConverter.ToString(sha.ComputeHash(f)).Replace("-",""),digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Контрольная сумма архива не совпала.");
            f.Position=0;
            using(var archive=new ZipArchive(f,ZipArchiveMode.Read,true))Activate(root,version,archive);
        }
    }
    static void Activate(string root,string version,ZipArchive archive){
        // Publish a complete version directory before atomically changing the launcher pointer.
        using(var gate=new FileStream(Path.Combine(root,"update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
            if(new Version(Boot.Active(root))>=new Version(version))return;
            string builds=Path.Combine(root,"builds"),stage=Path.Combine(builds,"stage-"+Guid.NewGuid().ToString("N")),target=Path.Combine(builds,version);
            Directory.CreateDirectory(stage);string pointer=Path.Combine(root,"version-"+Guid.NewGuid().ToString("N")+".tmp");
            try{
                {
                    foreach(string file in Files){
                        var entry=archive.GetEntry("Blink/builds/"+version+"/"+file);
                        if(entry==null||entry.Length<=0||entry.Length>256L*1024*1024)throw new InvalidDataException("Неполный пакет: "+file);
                        entry.ExtractToFile(Path.Combine(stage,file));
                    }
                }
                if(!Directory.Exists(target))Directory.Move(stage,target);
                else foreach(string file in Files){using(var hash=SHA256.Create())using(var x=File.OpenRead(Path.Combine(stage,file)))using(var y=File.OpenRead(Path.Combine(target,file)))if(!hash.ComputeHash(x).SequenceEqual(hash.ComputeHash(y)))throw new IOException("Конфликт каталога сборки.");}
                File.WriteAllText(pointer,version);File.Replace(pointer,Path.Combine(root,"version.txt"),null);
            }finally{File.Delete(pointer);if(Directory.Exists(stage)){foreach(string name in Files)File.Delete(Path.Combine(stage,name));Directory.Delete(stage);}}
        }
    }
    const int MaxPackage=192*1024*1024;
    static void Fetch(string url,Stream output,long limit){
        var request=(HttpWebRequest)WebRequest.Create(url);
        request.UserAgent="Blink-Updater/"+Boot.Current;
        request.Timeout=15000;request.ReadWriteTimeout=30000;
        using(var response=(HttpWebResponse)request.GetResponse()){
            if(response.ResponseUri.Scheme!=Uri.UriSchemeHttps||response.ContentLength>limit)throw new InvalidDataException("Некорректный ответ сервера обновлений.");
            using(var input=response.GetResponseStream()){
                byte[] buffer=new byte[65536];long received=0;int count;
                while((count=input.Read(buffer,0,buffer.Length))!=0){
                    received+=count;if(received>limit)throw new InvalidDataException("Размер обновления превысил ожидаемый.");
                    output.Write(buffer,0,count);
                }
            }
        }
    }
    internal static string Check(){
        string root=Boot.Root();if(root==null)return "Для обновлений запустите Blink.exe из распакованного релиза.";
        using(var meta=new MemoryStream()){
            Fetch("https://api.github.com/repos/uvi0n/Blink/releases/latest",meta,1024*1024);
            var release=new JavaScriptSerializer().Deserialize<Release>(Encoding.UTF8.GetString(meta.ToArray()));
            if(release.draft||release.prerelease||!Regex.IsMatch(release.tag_name??"",@"^v[0-9]+\.[0-9]+\.[0-9]+$"))throw new InvalidDataException("Релиз не распознан.");
            string version=release.tag_name.Substring(1),active=Boot.Active(root);
            if(new Version(active)>=new Version(version))return active==Boot.Current?"Установлена актуальная версия "+active+".":"Версия "+active+" готова. Откройте Blink заново.";
            var asset=release.assets.Single(a=>a.name=="Blink-Windows-x64.zip");
            string url="https://github.com/uvi0n/Blink/releases/download/v"+version+"/Blink-Windows-x64.zip";
            if(asset.browser_download_url!=url||!Regex.IsMatch(asset.digest??"",@"^sha256:[a-fA-F0-9]{64}$"))throw new InvalidDataException("Не удалось проверить источник обновления.");
            string tmp=Path.GetTempFileName();try{using(var output=new FileStream(tmp,FileMode.Truncate,FileAccess.Write,FileShare.None))Fetch(url,output,MaxPackage);Install(root,version,tmp,asset.digest);}finally{File.Delete(tmp);}
            return "Обновление "+version+" готово. Откройте Blink заново.";
        }
    }
}
