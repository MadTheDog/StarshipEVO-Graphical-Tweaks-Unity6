using System;
using SEVO.GlassRenderingProbe;
int checks=0;
void Is(GlassIdenticalOutcome expected,int covered,int changed,int bad=0,int range=0)
{checks++;if(GlassComparisonOutcome.Identical(covered,changed,bad,range)!=expected)throw new Exception("Parity outcome "+checks);}
Is(GlassIdenticalOutcome.Inconclusive,0,0);
Is(GlassIdenticalOutcome.Inconclusive,15,0);
Is(GlassIdenticalOutcome.Equivalent,16,0);
Is(GlassIdenticalOutcome.Equivalent,1000000,0);
Is(GlassIdenticalOutcome.Different,16,1);
Is(GlassIdenticalOutcome.Different,16,0,1);
Is(GlassIdenticalOutcome.Different,16,0,0,1);
Is(GlassIdenticalOutcome.Different,0,0,1);
if(GlassComparisonOutcome.Identical(100,0,0,0,true,0)!=GlassIdenticalOutcome.Inconclusive)throw new Exception("Empty sunlight loss accepted");checks++;
if(GlassComparisonOutcome.Identical(100,0,0,0,true,16)!=GlassIdenticalOutcome.Equivalent)throw new Exception("Nontrivial sunlight loss rejected");checks++;
Console.WriteLine("PASS "+checks+" interpolation-outcome checks");
