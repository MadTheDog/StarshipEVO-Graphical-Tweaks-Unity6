using System;
using System.Collections.Generic;
using SEVO.RenderingPerformanceProbe;
int checks=0;
var slots=new Dictionary<uint,GlassPartialReferenceDescriptor>{{1,new(50,3)}};
void Check(int expected,int refs,int[] words,int count=1,Dictionary<uint,GlassPartialReferenceDescriptor> map=null)
{int bad=GlassPartialReferenceChecks.Inspect(words,count,id=>id==1,map??slots,out int got);checks++;if(bad!=expected||got!=refs)throw new Exception("Reference check "+checks+": expected "+expected+"/"+refs+", got "+bad+"/"+got);}
Check(1,2,new[]{0,0,259,50,(257<<8)|3,50});
Check(1,2,new[]{0,0,259,50,259,50});
Check(0,1,new[]{0,0,259,50,3,50});
Check(1,1,new[]{0,0,259,51});
Check(1,1,new[]{0,0,260,50});
Check(1,1,new[]{0,0,259,50},1,new());
Check(1,1,new[]{0,0,259,50},0);
Check(0,1,new[]{0,0,256<<8,90},256,new(){{256,new(90,0)}});
Check(1,1,new[]{0,0,unchecked((int)0xFFFFFF00),50},256);
Check(0,0,Array.Empty<int>());
bool threw=false;try{GlassPartialReferenceChecks.Inspect(new[]{1},1,_=>true,slots,out _);}catch(ArgumentException){threw=true;}checks++;if(!threw)throw new Exception("Odd mirror accepted");
Console.WriteLine("PASS "+checks+" complete-mirror partial-reference checks");
