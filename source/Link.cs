using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// Each attempt owns its process. Callback identity is checked on the desktop UI thread.
sealed class Link : IDisposable {
    readonly Session session;
    readonly CancellationTokenSource cancel=new CancellationTokenSource();
    readonly object gate=new object();
    Process child;
    public Link(Session session){this.session=session;}
    static bool Check(string root,string key){
        try{
            var req=(HttpWebRequest)WebRequest.Create(root+"/"+key+"/probe");
            req.Timeout=req.ReadWriteTimeout=5000;req.AllowAutoRedirect=false;req.KeepAlive=false;
            using(var res=(HttpWebResponse)req.GetResponse())
            using(var stream=new StreamReader(res.GetResponseStream())){
                string expected="blink-"+key.Substring(0,24);
                if(res.StatusCode!=HttpStatusCode.OK||res.ContentLength>256)return false;
                char[] data=new char[expected.Length+1];int n=stream.ReadBlock(data,0,data.Length);
                return new string(data,0,n)==expected;
            }
        }catch(WebException){return false;}catch(IOException){return false;}
    }
    public async Task Run(Action<string,string> update){
        var token=cancel.Token;
        for(int attempt=1;attempt<=3;attempt++){
            token.ThrowIfCancellationRequested();
            string ssh=Path.Combine(Environment.SystemDirectory,@"OpenSSH\ssh.exe");
            bool alternative=attempt!=2 && File.Exists(ssh);
            string provider=alternative?"localhost.run":"Cloudflare";
            update(null,"Проверяем "+provider+" · попытка "+attempt+" из 3");
            string candidate=null;
            using(var p=new Process()){
                string app=AppDomain.CurrentDomain.BaseDirectory;
                string sshArgs="-T -F NUL -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityFile=none -o IdentityAgent=none -o PreferredAuthentications=none -o UserKnownHostsFile=lhr_hosts -o StrictHostKeyChecking=yes -o HostKeyAlgorithms=ssh-ed25519 -o ConnectTimeout=8 -o ExitOnForwardFailure=yes -o ServerAliveInterval=20 -R 80:127.0.0.1:"+new Uri(session.Local).Port+" nokey@localhost.run";
                p.StartInfo=new ProcessStartInfo(alternative?ssh:Path.Combine(app,"cloudflared.exe"),alternative?sshArgs:"tunnel --protocol http2 --no-autoupdate --url "+session.Local+" --http-host-header "+new Uri(session.Local).Authority){WorkingDirectory=app,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
                DataReceivedEventHandler read=delegate(object o,DataReceivedEventArgs e){
                    if(e.Data==null || (alternative && !e.Data.Contains("tunneled with tls termination")))return;
                    var m=Regex.Match(e.Data,alternative?@"https://([a-z0-9-]+)\.(?:app\.)?lhr\.life\b":@"https://([a-z0-9-]+)\.trycloudflare\.com\b");
                    if(m.Success && m.Groups[1].Value!="api")Interlocked.Exchange(ref candidate,m.Value);
                };
                p.OutputDataReceived+=read;p.ErrorDataReceived+=read;
                try{
                    lock(gate){token.ThrowIfCancellationRequested();p.Start();child=p;}
                    p.BeginErrorReadLine();p.BeginOutputReadLine();
                    DateTime deadline=DateTime.UtcNow.AddSeconds(40);
                    bool published=false;
                    while(!p.HasExited && (published||DateTime.UtcNow<deadline)){
                        token.ThrowIfCancellationRequested();
                        string host=Interlocked.CompareExchange(ref candidate,null,null);
                        bool healthy=host!=null && await Task.Run(()=>Check(host,session.Key));
                        if(healthy){published=true;update(host+"/"+session.Key+"/","Ссылка проверена • "+provider+"");}
                        else if(published)update(null,"Связь прервалась. Проверяем прежний адрес…");
                        await Task.Delay(healthy?25000:1800,token);
                    }
                    if(published)throw new IOException("Туннель закрылся. Создайте новую ссылку для этих файлов.");
                }finally{lock(gate){Kill();child=null;}}
            }
        }
        throw new IOException("Сервис не подтвердил доступ к файлам. Проверьте сеть и повторите.");
    }
    void Kill(){if(child!=null)try{if(!child.HasExited)child.Kill();}catch(InvalidOperationException){}}
    public void Dispose(){cancel.Cancel();lock(gate)Kill();}
}

