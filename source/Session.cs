using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Globalization;

public sealed class Session : IDisposable {
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback,0);
    readonly object uploadLock = new object();
    readonly Semaphore slots = new Semaphore(8,8);
    readonly string path, partial;
    readonly bool receiving, folderMode; string receivedPath;
    readonly System.Collections.Generic.List<FileStream> sources=new System.Collections.Generic.List<FileStream>();
    volatile bool closed, complete;
    long total = -1, offset, previousOffset = -1, previousCount; string previousDigest;
    readonly Transfers transfers=new Transfers();
    public string Outgoing {get{return transfers.Summary();}}
    public readonly string Local, Key;
    public readonly DateTime Expires;
    public Session(string file, bool receive) : this(new[]{file},receive) {}
    public Session(string[] files, bool receive,int hours=12) {
        Expires=DateTime.UtcNow.AddHours(hours);
        string file=files[0];
        path=file; receiving=receive;
        byte[] random=new byte[32]; using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(random);
        Key=BitConverter.ToString(random).Replace("-","").ToLowerInvariant();
        folderMode=receive && Directory.Exists(file);
        partial=folderMode?Path.Combine(file,".blink-"+Key+".partial"):file+"."+Key.Substring(0,12)+".partial";
        try {
            if(receive && File.Exists(file)) throw new IOException("Выберите новое имя файла.");
            if(!receive) foreach(string name in files) sources.Add(new FileStream(name,FileMode.Open,FileAccess.Read,FileShare.Read));
            listener.Start(16); Local="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
        } catch { Dispose(); throw; }
        ThreadPool.QueueUserWorkItem(delegate { Listen(); });
    }
    void Listen() {
        while(!closed) try {
            var client=listener.AcceptTcpClient();
            if(!slots.WaitOne(0)){client.Close();continue;}
            client.ReceiveTimeout=20000;client.SendTimeout=30000;
            ThreadPool.QueueUserWorkItem(delegate {using(client){try {Handle(new LocalContext(client));} catch(IOException){} catch(Exception){} finally {slots.Release();}}});        } catch { if(!closed) Thread.Sleep(50); }
    }
    static void Reply(LocalContext c,int status,string text,string mime="text/plain; charset=utf-8") {
        var data=Encoding.UTF8.GetBytes(text); c.Response.StatusCode=status;c.Response.ContentType=mime;c.Response.ContentLength64=data.Length;
        if(c.Request.HttpMethod!="HEAD") c.Response.OutputStream.Write(data,0,data.Length);
        c.Response.Close();
    }
    void Handle(LocalContext c) {
        var q=c.Request;var r=c.Response;
        r.Headers["Cache-Control"]="private, no-store";r.Headers["Referrer-Policy"]="no-referrer";
        r.Headers["X-Content-Type-Options"]="nosniff";r.Headers["X-Frame-Options"]="DENY";
        r.Headers["Content-Security-Policy"]="default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        string prefix="/"+Key+"/", url=q.Path;
        if(closed || DateTime.UtcNow>=Expires || !url.StartsWith(prefix,StringComparison.Ordinal)){Reply(c,404,"Ссылка недоступна.");return;}
        string route=url.Substring(prefix.Length);
        if(receiving && route=="chunk" && q.HttpMethod=="POST"){Upload(c);return;}
        if(!receiving && route=="selected.zip" && q.HttpMethod=="POST"){
            if(q.ContentLength64<1 || q.ContentLength64>sources.Count*24L || q.ContentLength64>int.MaxValue){Reply(c,400,"Выберите файлы.");return;}
            var data=new byte[(int)q.ContentLength64];int pos=0;
            while(pos<data.Length){int n=q.InputStream.Read(data,pos,data.Length-pos);if(n==0)throw new IOException();pos+=n;}
            var ids=new System.Collections.Generic.SortedSet<int>();
            foreach(string pair in Encoding.ASCII.GetString(data).Split('&')){
                int id;if(!pair.StartsWith("id=")||!int.TryParse(pair.Substring(3),NumberStyles.None,CultureInfo.InvariantCulture,out id)||id>=sources.Count){Reply(c,400,"Неверный выбор файлов.");return;}ids.Add(id);
            }
            SendBundle(c,System.Linq.Enumerable.Select(ids,id=>sources[id]));return;
        }
        if(q.HttpMethod!="GET" && q.HttpMethod!="HEAD"){Reply(c,405,"");return;}
        if(route==""){
            var list=new StringBuilder();
            for(int i=0;i<sources.Count;i++){
                var item=sources[i];
                list.Append("<li><input type=checkbox name=id form=selection aria-label=\"Выбрать: ").Append(WebUtility.HtmlEncode(Path.GetFileName(item.Name))).Append("\" value=").Append(i).Append("><div><strong>").Append(WebUtility.HtmlEncode(Path.GetFileName(item.Name))).Append("</strong><small>").Append(SizeText(item.Length)).Append("</small></div><a download href='file/").Append(i).Append("'>Скачать</a></li>");
            }
            string html=Resource("page.html").Replace("@MODE@",receiving?"receive":"send").Replace("@FILES@",list.ToString()).Replace("@TARGET@",folderMode?"Исходное имя файла":WebUtility.HtmlEncode(Path.GetFileName(path)));
            Reply(c,200,html,"text/html; charset=utf-8");
        } else if(route=="ui.js") Reply(c,200,Resource("ui.js"),"application/javascript; charset=utf-8");
        else if(!receiving && route=="all.zip"){
            SendBundle(c,sources);
        }
        else if(route=="probe") Reply(c,200,"blink-"+Key.Substring(0,24));
        else if(!receiving && (route=="file"||route.StartsWith("file/"))){
            int index=0;
            if(route!="file" && (!Int32.TryParse(route.Substring(5),System.Globalization.NumberStyles.None,CultureInfo.InvariantCulture,out index)||index>=sources.Count)){Reply(c,404,"");return;}
            Download(c,sources[index],index);
        }
        else Reply(c,404,"");
    }
    void SendBundle(LocalContext c,System.Collections.Generic.IEnumerable<FileStream> files){
        var bundle=new Bundle(files);var r=c.Response;r.ContentType="application/zip";r.Headers["Content-Disposition"]="attachment; filename=Blink.zip";r.ContentLength64=bundle.Length;
        if(c.Request.HttpMethod=="HEAD"){r.Close();return;}
        var t=transfers.Begin("Архив",bundle.PayloadSize);bool done=false;
        try{bundle.Send(r.OutputStream,()=>!closed && DateTime.UtcNow<Expires,(name,n)=>transfers.Report(t,name,n));r.Close();done=true;}
        finally{transfers.End(t,done);}
    }
    static string Resource(string name) { using(var s=typeof(Session).Assembly.GetManifestResourceStream(name)) using(var reader=new StreamReader(s))return reader.ReadToEnd(); }
    public static string SizeText(long bytes){
        double n=bytes; string[] units={"Б","КиБ","МиБ","ГиБ","ТиБ","ПиБ"};int i=0;
        while(n>=1024 && i<units.Length-1){n/=1024;i++;}return n.ToString("0.#")+" "+units[i];
    }
    void Download(LocalContext c,FileStream source,int index) {
        string path=source.Name;
        long length=source.Length, first=0,last=length-1;var r=c.Response;
        var range=c.Request.Headers["Range"];
        if(range!=null){
            var match=Regex.Match(range,@"^bytes=(\d*)-(\d*)$");
            long n;
            bool ok=match.Success;
            if(ok && match.Groups[1].Value==""){
                ok=long.TryParse(match.Groups[2].Value,out n)&&n>0;first=Math.Max(0,length-n);
            }else if(ok){
                ok=long.TryParse(match.Groups[1].Value,out first);
                if(match.Groups[2].Value!=""){ok=ok&&long.TryParse(match.Groups[2].Value,out last);last=Math.Min(last,length-1);}
            }
            if(!ok||first>last||first>=length){r.Headers["Content-Range"]="bytes */"+length;Reply(c,416,"");return;}
            r.StatusCode=206;r.Headers["Content-Range"]="bytes "+first+"-"+last+"/"+length;
        }
        r.Headers["Accept-Ranges"]="bytes";r.Headers["ETag"]="\""+Key.Substring(0,16)+index+"\"";
        r.Headers["Content-Disposition"]="attachment; filename=file; filename*=UTF-8''"+Uri.EscapeDataString(Path.GetFileName(path));
        long remaining=last-first+1;r.ContentLength64=remaining;r.ContentType="application/octet-stream";
        if(c.Request.HttpMethod=="HEAD"){r.Close();return;}
        var t=transfers.Begin(Path.GetFileName(path),remaining);bool done=false;
        try{using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
            input.Position=first;byte[] buf=new byte[65536];
            while(remaining>0 && !closed && DateTime.UtcNow<Expires){int n=input.Read(buf,0,(int)Math.Min(remaining,buf.Length));if(n==0)break;r.OutputStream.Write(buf,0,n);remaining-=n;transfers.Report(t,t.Name,n);}
        }
        r.Close();done=remaining==0;}finally{transfers.End(t,done);}
    }
    void Upload(LocalContext c) {
        lock(uploadLock){
            var q=c.Request;long at,size, count=q.ContentLength64;
            if(!closed && long.TryParse(q.Headers["X-Offset"],out at) && at==previousOffset && previousOffset>=0 && count==previousCount && long.TryParse(q.Headers["X-Size"],out size) && size==total && String.Equals(q.Headers["X-SHA256"],previousDigest,StringComparison.OrdinalIgnoreCase)){
                byte[] discard=new byte[65536];long left=count;while(left>0){int read=q.InputStream.Read(discard,0,(int)Math.Min(left,discard.Length));if(read==0)throw new IOException();left-=read;} Reply(c,200,offset.ToString(CultureInfo.InvariantCulture));return;
            }
            if(closed||complete||!long.TryParse(q.Headers["X-Offset"],out at)||!long.TryParse(q.Headers["X-Size"],out size)||size<0||at!=offset||count<0||count>4194304||count>size-at||(count==0&&size!=0)||(total!=-1&&total!=size)){
                Reply(c,409,"Передача занята или смещение изменилось.");return;
            }
            if(Path.GetPathRoot(path).Length==3 && new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace-67108864<size-at){Reply(c,507,"Недостаточно места на компьютере.");return;}
            byte[] data=new byte[(int)count];int used=0;
            while(used<data.Length){int n=q.InputStream.Read(data,used,data.Length-used);if(n==0){Reply(c,400,"Неполный блок.");return;}used+=n;}
            using(var hash=SHA256.Create()) if(!String.Equals(BitConverter.ToString(hash.ComputeHash(data)).Replace("-",""),q.Headers["X-SHA256"],StringComparison.OrdinalIgnoreCase)){Reply(c,400,"Ошибка проверки блока.");return;}
            if(closed){Reply(c,410,"");return;}
            if(folderMode && receivedPath==null){
                string name=Uri.UnescapeDataString(q.Headers["X-Name"]??"").Trim();
                if(name.Length==0 || name.Length>215 || name.StartsWith(".") || name.EndsWith(".") || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || Regex.IsMatch(name,@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³]) *(\.|$)",RegexOptions.IgnoreCase)){
                    Reply(c,400,"Выберите другое имя файла.");return;
                }
                if(name.Equals("desktop.ini",StringComparison.OrdinalIgnoreCase)||name.Equals("autorun.inf",StringComparison.OrdinalIgnoreCase)) name="Полученный "+name;
                receivedPath=Path.Combine(path,name);
            }
            using(var f=new FileStream(partial,FileMode.OpenOrCreate,FileAccess.Write,FileShare.None)){f.Position=at;f.Write(data,0,used);f.SetLength(at+used);}
            previousOffset=at;previousCount=used;previousDigest=q.Headers["X-SHA256"];offset+=used;total=size;
            if(offset==size){try{if(folderMode)SaveInFolder();else File.Move(partial,path);complete=true;}catch{previousOffset=-1;offset=at;throw;}}
            Reply(c,200,offset.ToString(CultureInfo.InvariantCulture));
        }
    }
    void SaveInFolder(){
        string target=receivedPath;
        for(int suffix=1;;suffix++){
            try{File.Move(partial,target);receivedPath=target;return;}
            catch(IOException){if(!File.Exists(target)&&!Directory.Exists(target))throw;}
            target=Path.Combine(path,Path.GetFileNameWithoutExtension(receivedPath)+" ("+suffix+")"+Path.GetExtension(receivedPath));
        }
    }
    public string Status { get {return complete?"Сохранено: "+(receivedPath??path):(receiving && total>=0?"Получено "+Interlocked.Read(ref offset).ToString("N0")+" байт":null);} }
    public void Dispose(){closed=true;try{listener.Stop();}catch{} foreach(var source in sources)source.Dispose();
        if(receiving) ThreadPool.QueueUserWorkItem(delegate{lock(uploadLock){try{File.Delete(partial);}catch{}}});}
}




