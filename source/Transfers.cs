using System;
using System.Linq;
using System.Collections.Generic;

internal sealed class Transfers {
    internal sealed class Item {
        public string Name;
        public long Total, Sent;
        public string Phase="Передаётся";
    }
    readonly List<Item> active=new List<Item>();
    readonly Queue<Item> finished=new Queue<Item>();
    public Item Begin(string name,long size){lock(active){var t=new Item{Name=name,Total=size};active.Add(t);return t;}}
    public void Report(Item t,string file,int count){lock(active){t.Name=file;t.Sent+=count;}}
    public void End(Item t,bool ok){lock(active){active.Remove(t);t.Phase=ok?"Отправлено":"Прервано";finished.Enqueue(t);if(finished.Count>100)finished.Dequeue();}}
    public string Summary(){lock(active){
        var rows=active.Concat(finished.Reverse()).ToArray();
        return string.Join(Environment.NewLine,rows.Select(t=>t.Phase+" · "+t.Name+" · "+Session.SizeText(t.Sent)+" / "+Session.SizeText(t.Total)+" · "+(t.Total==0?(t.Phase=="Отправлено"?100:0):(int)(100.0*t.Sent/t.Total))+"%"));
    }}
}
