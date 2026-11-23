using System;
class Harness{
 static void Main(string[] args){using(var s=new Session(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Skip(args,1)),args[0]=="receive")){Console.WriteLine(s.Local+"/"+s.Key+"/");Console.ReadLine();}}
}

