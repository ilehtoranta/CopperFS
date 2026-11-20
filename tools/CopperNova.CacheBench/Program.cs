using System.Text.Json;
using CopperNova.CacheBench;

var run=new CachePressureScenario();
if(args.Length==2&&args[0]=="measure") run.UseSeed(File.ReadAllBytes(args[1]));
else { run.Seed(); if(args.Length==1) File.WriteAllBytes(args[0],run.Disk); }
var result=run.Measure();
Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
