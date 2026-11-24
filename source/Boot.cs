using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;

internal static class Boot {
    public const string Current="7.1.0";
    public static string Active(string root){
        string s=File.ReadAllText(Path.Combine(root,"version.txt")).Trim();
        if(!Regex.IsMatch(s,@"^[0-9]+\.[0-9]+\.[0-9]+$"))throw new IOException("Неверный номер сборки Blink.");
        return s;
    }
    public static string Root(){
        var dir=new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        return dir.Parent!=null && dir.Parent.Name=="builds" && dir.Parent.Parent!=null?dir.Parent.Parent.FullName:null;
    }
}
