using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
class Verify {
 static void Main(){ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;Run().GetAwaiter().GetResult();}
 static async Task Run(){
    string folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
    Directory.CreateDirectory(folder);
    string a=Path.Combine(folder,"Первый & файл.txt"),b=Path.Combine(folder,"second.bin");
    try{File.WriteAllText(a,"alpha",new UTF8Encoding(false));File.WriteAllBytes(b,new byte[]{8,2,4,6});
    using(var session=new Session(new[]{a,b},false))using(var tunnel=new Link(session)){
        var ready=new TaskCompletionSource<string>();
        var run=tunnel.Run((url,status)=>{Console.WriteLine(status);if(url!=null)ready.TrySetResult(url);});
        var winner=await Task.WhenAny(run,ready.Task);
        if(winner==run)await run;
        string root=await ready.Task;
        using(var web=new WebClient()){web.Encoding=Encoding.UTF8;
          string html=web.DownloadString(root);
          if(!html.Contains("file/0")||!html.Contains("file/1")||!html.Contains("&amp;"))throw new Exception("Wrong listing");
          using(var zip=new System.IO.Compression.ZipArchive(new MemoryStream(web.DownloadData(root+"all.zip")))){
            if(zip.Entries.Count!=2)throw new Exception("Wrong ZIP entry count");
            using(var input=zip.Entries[0].Open())using(var text=new StreamReader(input))if(text.ReadToEnd()!="alpha")throw new Exception("ZIP content changed");
            using(var input=zip.Entries[1].Open())using(var bytes=new MemoryStream()){input.CopyTo(bytes);if(BitConverter.ToString(bytes.ToArray())!="08-02-04-06")throw new Exception("ZIP content changed");}
          }
          using(var subset=new System.IO.Compression.ZipArchive(new MemoryStream(web.UploadData(root+"selected.zip","POST",Encoding.ASCII.GetBytes("id=1"))))){
            if(subset.Entries.Count!=1||subset.Entries[0].FullName!="second.bin")throw new Exception("Wrong subset");
            using(var input=subset.Entries[0].Open())using(var copy=new MemoryStream()){input.CopyTo(copy);if(BitConverter.ToString(copy.ToArray())!="08-02-04-06")throw new Exception("Wrong selected data");}
          }
          if(web.DownloadString(root+"file/0")!="alpha"||BitConverter.ToString(web.DownloadData(root+"file/1"))!="08-02-04-06")throw new Exception("Wrong data");
        }if(!session.Outgoing.Contains("100%")||!session.Outgoing.Contains("second.bin"))throw new Exception("Progress missing");Console.WriteLine("PASS: public selection ZIP, all files, exact bytes and sender completion");tunnel.Dispose();try{await run;}catch(OperationCanceledException){}
    }}finally{File.Delete(a);File.Delete(b);Directory.Delete(folder);}
 }
}



