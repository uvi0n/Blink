using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Updates {
    internal sealed class Asset { public string name{get;set;} public string digest{get;set;} public string browser_download_url{get;set;} }
    internal sealed class Release { public string tag_name{get;set;} public Asset[] assets{get;set;} public bool prerelease{get;set;} public bool draft{get;set;} }
    internal static readonly string[] Files={"Blink.exe","Blink.exe.config","QRCoder.dll","age.exe","cloudflared.exe","lhr_hosts"};
    internal static void Install(string root,string version,string zip,string digest){
        if(!Regex.IsMatch(version,@"^[0-9]+\.[0-9]+\.[0-9]+$"))throw new InvalidDataException("Некорректная версия.");
        using(var f=File.OpenRead(zip))using(var sha=SHA256.Create()){
            if(!string.Equals("sha256:"+BitConverter.ToString(sha.ComputeHash(f)).Replace("-",""),digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Контрольная сумма архива не совпала.");
        }
        // Publish a complete version directory before atomically changing the launcher pointer.
        using(var gate=new FileStream(Path.Combine(root,"update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
            if(new Version(Boot.Active(root))>=new Version(version))return;
            string builds=Path.Combine(root,"builds"),stage=Path.Combine(builds,"stage-"+Guid.NewGuid().ToString("N")),target=Path.Combine(builds,version);
            Directory.CreateDirectory(stage);string pointer=Path.Combine(root,"version-"+Guid.NewGuid().ToString("N")+".tmp");
            try{
                using(var a=ZipFile.OpenRead(zip)){
                    foreach(string file in Files){
                        var entry=a.GetEntry("Blink/builds/"+version+"/"+file);
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
    internal static string Check(){
        string root=Boot.Root();if(root==null)return "Для обновлений запустите Blink.exe из распакованного релиза.";
        using(var web=new WebClient()){
            web.Headers["User-Agent"]="Blink-Updater/"+Boot.Current;
            var release=new JavaScriptSerializer().Deserialize<Release>(web.DownloadString("https://api.github.com/repos/uvi0n/Blink/releases/latest"));
            if(release.draft||release.prerelease||!Regex.IsMatch(release.tag_name??"",@"^v[0-9]+\.[0-9]+\.[0-9]+$"))throw new InvalidDataException("Релиз не распознан.");
            string version=release.tag_name.Substring(1),active=Boot.Active(root);
            if(new Version(active)>=new Version(version))return active==Boot.Current?"Установлена актуальная версия "+active+".":"Версия "+active+" готова. Откройте Blink заново.";
            var asset=release.assets.Single(a=>a.name=="Blink-Windows-x64.zip");
            string url="https://github.com/uvi0n/Blink/releases/download/v"+version+"/Blink-Windows-x64.zip";
            if(asset.browser_download_url!=url||!Regex.IsMatch(asset.digest??"",@"^sha256:[a-fA-F0-9]{64}$"))throw new InvalidDataException("Не удалось проверить источник обновления.");
            string tmp=Path.GetTempFileName();try{web.DownloadFile(url,tmp);Install(root,version,tmp,asset.digest);}finally{File.Delete(tmp);}
            return "Обновление "+version+" готово. Откройте Blink заново.";
        }
    }
}
