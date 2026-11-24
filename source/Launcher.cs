using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Windows.Forms;

internal static class Launcher {
    static string Quote(string value){return "\""+Regex.Replace(value,@"(\\*)(""|$)",m=>m.Groups[1].Value+m.Groups[1].Value+(m.Groups[2].Value.Length==0?"":"\\\""))+"\"";}
    [STAThread] static void Main(string[] args){
        try{string root=AppDomain.CurrentDomain.BaseDirectory;
            string app=Path.Combine(root,"builds",Boot.Active(root),"Blink.exe");
            Process.Start(new ProcessStartInfo(app,string.Join(" ",args.Select(Quote))){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(app)});
        }catch(Exception ex){MessageBox.Show(ex.Message,"Не удалось открыть Blink");}
    }
}
