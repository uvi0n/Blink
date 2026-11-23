using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Specialized;

// A single HTTP/1.x request per loopback connection. Only fixed-length bodies are accepted.
sealed class LocalRequest {
    public string HttpMethod, Path;
    public long ContentLength64;
    public readonly NameValueCollection Headers=new NameValueCollection(StringComparer.OrdinalIgnoreCase);
    public readonly Stream InputStream;
    public LocalRequest(Stream stream){
        InputStream=stream;var bytes=new MemoryStream();int marker=0;
        byte[] end={13,10,13,10};
        while(marker<4){int b=stream.ReadByte();if(b<0||bytes.Length>=16384)throw new IOException();bytes.WriteByte((byte)b);marker=b==end[marker]?marker+1:(b==13?1:0);}
        string[] lines=Encoding.ASCII.GetString(bytes.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);
        string[] request=lines[0].Split(' ');if(request.Length!=3 || !request[1].StartsWith("/") || !request[2].StartsWith("HTTP/1."))throw new IOException();
        HttpMethod=request[0];Path=request[1];
        for(int i=1;i<lines.Length && lines[i]!="";i++){int split=lines[i].IndexOf(':');if(split<1)throw new IOException();string key=lines[i].Substring(0,split);Headers.Add(key,lines[i].Substring(split+1).Trim());}
        if(Headers["Transfer-Encoding"]!=null || (Headers["Content-Length"]!=null && (!long.TryParse(Headers["Content-Length"],out ContentLength64)||ContentLength64<0)))throw new IOException();
    }
}
sealed class LocalResponse {
    readonly TcpClient client;bool sent;
    public int StatusCode=200;public long ContentLength64;public string ContentType="text/plain";
    public readonly NameValueCollection Headers=new NameValueCollection();
    public LocalResponse(TcpClient c){client=c;}
    public Stream OutputStream {get{var stream=client.GetStream();if(!sent){
        var h=new StringBuilder("HTTP/1.1 "+StatusCode+" Response\r\nConnection: close\r\n");
        h.Append("Content-Length: "+ContentLength64+"\r\nContent-Type: "+ContentType+"\r\n");
        foreach(string key in Headers)h.Append(key+": "+Headers[key]+"\r\n");
        byte[] b=Encoding.ASCII.GetBytes(h.ToString()+"\r\n");stream.Write(b,0,b.Length);sent=true;
    }return stream;}}
    public void Close(){try{OutputStream.Flush();}finally{client.Close();}}
}
sealed class LocalContext {
    public LocalRequest Request;public LocalResponse Response;
    public LocalContext(TcpClient c){Request=new LocalRequest(c.GetStream());Response=new LocalResponse(c);}
}
