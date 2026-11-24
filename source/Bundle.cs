using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.CompilerServices;

// Stored ZIP64 entries have known lengths. CRCs are calculated while sending each file.
internal sealed class Bundle {
    sealed class Item { public FileStream Source; public byte[] Name; public long Size,Offset; public uint Crc; }
    readonly List<Item> items=new List<Item>();
    readonly long centralOffset,centralLength;
    public long PayloadSize {get;private set;}
    public long Length {get;private set;}
    sealed class Checksum { public readonly uint Value; public Checksum(uint value){Value=value;} }
    // Source handles are locked for the session. A new session always gets new cache keys.
    static readonly ConditionalWeakTable<FileStream,Checksum> checksums=new ConditionalWeakTable<FileStream,Checksum>();
    static readonly uint[] crcTable=MakeTable();
    static uint[] MakeTable(){
        var table=new uint[256];
        for(uint n=0;n<256;n++){uint v=n;for(int bit=0;bit<8;bit++)v=(v&1)==0?v>>1:(v>>1)^0xedb88320U;table[n]=v;}
        return table;
    }
    public Bundle(IEnumerable<FileStream> files){
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files){
            string original=Path.GetFileName(file.Name),name=original;
            for(int i=2;!used.Add(name);i++)name=Path.GetFileNameWithoutExtension(original)+" ("+i+")"+Path.GetExtension(original);
            var item=new Item{Source=file,Size=file.Length,Name=Encoding.UTF8.GetBytes(name),Offset=centralOffset};
            items.Add(item);
            checked {PayloadSize+=item.Size;centralOffset+=30+item.Name.Length+20+item.Size+24;centralLength+=46+item.Name.Length+28;}
        }
        Length=checked(centralOffset+centralLength+98);
    }
    static void Common(BinaryWriter w){w.Write((ushort)45);w.Write((ushort)0x0808);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)33);}
    static void Sizes(BinaryWriter w,Item item,bool central){
        w.Write((ushort)1);w.Write((ushort)(central?24:16));w.Write(item.Size);w.Write(item.Size);if(central)w.Write(item.Offset);
    }
    public void Send(Stream destination,Func<bool> active,Action<string,int> report){
        var range=destination as RangeSink;
        var w=new BinaryWriter(destination,Encoding.UTF8,true);var buffer=new byte[65536];
        foreach(var item in items){
            if(range!=null && range.Finished)return;
            if(!active())throw new IOException("Передача закрыта.");
            w.Write(0x04034b50U);Common(w);w.Write(0U);w.Write(uint.MaxValue);w.Write(uint.MaxValue);
            w.Write((ushort)item.Name.Length);w.Write((ushort)20);w.Write(item.Name);Sizes(w,item,false);
            string name=Path.GetFileName(item.Source.Name);report(name,0);uint crc=uint.MaxValue;
            Checksum cached;bool known=checksums.TryGetValue(item.Source,out cached);
            using(var source=new FileStream(item.Source.Name,FileMode.Open,FileAccess.Read,FileShare.Read)){
                if(known && range!=null)source.Position=range.SkipCachedPrefix(item.Size);
                int count;while((range==null||!range.Finished)&&(count=source.Read(buffer,0,buffer.Length))>0){
                    if(!active())throw new IOException("Передача закрыта.");
                    if(!known)for(int i=0;i<count;i++)crc=crcTable[(byte)(crc^buffer[i])]^(crc>>8);
                    w.Write(buffer,0,count);report(name,count);
                }
            }
            if(range!=null && range.Finished)return;
            item.Crc=known?cached.Value:checksums.GetValue(item.Source,ignored=>new Checksum(~crc)).Value;w.Write(0x08074b50U);w.Write(item.Crc);w.Write(item.Size);w.Write(item.Size);
        }
        foreach(var item in items){
            w.Write(0x02014b50U);w.Write((ushort)45);Common(w);w.Write(item.Crc);w.Write(uint.MaxValue);w.Write(uint.MaxValue);
            w.Write((ushort)item.Name.Length);w.Write((ushort)28);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)0);w.Write(0U);w.Write(uint.MaxValue);
            w.Write(item.Name);Sizes(w,item,true);
        }
        w.Write(0x06064b50U);w.Write(44L);w.Write((ushort)45);w.Write((ushort)45);w.Write(0U);w.Write(0U);
        w.Write((long)items.Count);w.Write((long)items.Count);w.Write(centralLength);w.Write(centralOffset);
        w.Write(0x07064b50U);w.Write(0U);w.Write(centralOffset+centralLength);w.Write(1U);
        w.Write(0x06054b50U);w.Write((ushort)0);w.Write((ushort)0);w.Write(ushort.MaxValue);w.Write(ushort.MaxValue);w.Write(uint.MaxValue);w.Write(uint.MaxValue);w.Write((ushort)0);w.Flush();
    }
}
