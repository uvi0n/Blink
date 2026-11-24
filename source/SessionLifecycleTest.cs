// Run from a directory containing Blink.exe:
// csc /r:Blink.exe SessionLifecycleTest.cs
// SessionLifecycleTest.exe
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Linq;

class SessionLifecycleTest {
    static void Check(bool pass,string message){if(!pass)throw new InvalidOperationException(message);}
    static string Init(WebClient client,string root){return Encoding.ASCII.GetString(client.UploadData(root+"new-upload","POST",new byte[0]));}
    static void Chunk(WebClient client,string root,string ticket,long offset,byte[] bytes){
        client.Headers["X-Size"]="4";
        client.Headers["X-Offset"]=offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        client.Headers["X-Name"]="sample.bin";
        using(var sha=SHA256.Create())client.Headers["X-SHA256"]=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
        string next=Encoding.ASCII.GetString(client.UploadData(root+"chunk/"+ticket,"POST",bytes));
        Check(next==(offset+bytes.Length).ToString(),"Incorrect offset acknowledgement");
    }
    static void Main(){
        string folder=Path.Combine(Path.GetTempPath(),"Blink-lifecycle-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try{
            using(var client=new WebClient())using(var s=new Session(folder,true)){
                s.Collect=true;
                string root=s.Local+"/"+s.Key+"/";
                string first=Init(client,root), second=Init(client,root);
                Chunk(client,root,first,0,new byte[]{1,2});
                Chunk(client,root,second,0,new byte[]{3,4});
                Chunk(client,root,second,2,new byte[]{5,6});
                Check(File.ReadAllBytes(Path.Combine(folder,"sample.bin")).SequenceEqual(new byte[]{3,4,5,6}),"Interleaved streams changed the completed file");
            }
            Check(Directory.GetFiles(folder,"*.partial").Length==0,"Unfinished upload remained after session revocation");
            using(var client=new WebClient())using(var s=new Session(Path.Combine(folder,"sample.bin"),false)){
                string root=s.Local+"/"+s.Key+"/";
                Check(client.DownloadString(root).Contains("data-bytes=\"4\""),"Missing exact size");
                byte[] png=client.DownloadData(root+"brand.png");
                Check(png.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"Missing embedded brand PNG");
                s.Authorize=delegate{return true;};
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(root);
                string cookie;
                try{using(req.GetResponse()){}throw new InvalidOperationException("Approval screen was not displayed");}
                catch(WebException e){using(var resp=(HttpWebResponse)e.Response){
                    Check(resp!=null && resp.StatusCode==HttpStatusCode.Forbidden,"Approval status mismatch");
                    cookie=resp.Headers["Set-Cookie"].Split(';')[0];
                }}
                var accept=(HttpWebRequest)WebRequest.Create(root+"request");accept.Method="POST";accept.ContentLength=0;accept.AllowAutoRedirect=false;accept.Headers["Cookie"]=cookie;
                using(var response=(HttpWebResponse)accept.GetResponse())Check(response.StatusCode==HttpStatusCode.SeeOther,"Grant not confirmed");
                client.Headers["Cookie"]=cookie;
                Check(client.DownloadData(root+"file/0").SequenceEqual(new byte[]{3,4,5,6}),"Approved download failed");
            }
            Console.WriteLine("PASS upload isolation, revoke cleanup, approval, branding and exact sizes");
        }finally{foreach(var file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);}
    }
}
