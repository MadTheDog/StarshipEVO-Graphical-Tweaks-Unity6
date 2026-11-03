using System;
using GraphicalTweaks;
class Program
{
 static int checks;
 static void Check(bool result,string message){checks++;if(!result)throw new Exception(message);}
 static void Main()
 {
  var p=new GlassUploadProof<int>(); var source=new object(); p.ChangeSource(source);
  Check(!p.Matches(7,44),"unwatched ID has no successful upload proof"); p.Watch(7);
  Check(!p.Matches(7,44),"watching CPU data is not an upload");
  long first=p.BeginWrite(7); p.CompleteWrite(7,first,44);
  Check(p.Matches(7,44),"successful upload establishes matching value");
  Check(!p.Matches(7,45),"changed geometry is rejected");
  long failed=p.BeginWrite(7);
  Check(!p.Matches(7,44),"failed upload must erase old proof even for same-source ID reuse");
  long newer=p.BeginWrite(7); p.CompleteWrite(7,failed,44);
  Check(!p.Matches(7,44),"obsolete completion cannot bless later write");
  p.CompleteWrite(7,newer,45); Check(p.Matches(7,45),"latest successful immediate value accepted");
  p.ChangeSource(new object()); Check(!p.Matches(7,45),"new GPU buffer generation rejects old upload");
  p.ConfirmSnapshot(7,46); Check(p.Matches(7,46),"completed native push establishes watched CPU snapshot");
  long removedTicket=p.BeginWrite(7);
  p.Forget(7); p.Watch(7); Check(!p.Matches(7,46),"freed and reused ID does not inherit proof");
  p.CompleteWrite(7,removedTicket,46);Check(!p.Matches(7,46),"late same-source upload completion cannot bless a freed and reused ID");
  p.Poison(); p.ConfirmSnapshot(7,46); Check(!p.Matches(7,46),"failed native immediate upload cannot be blessed by later no-op push");
  p.Clear();p.ChangeSource(source);p.Watch(7);p.ConfirmSnapshot(7,46);Check(!p.Matches(7,46),"toggle or dispose cannot erase native upload uncertainty");
  p.ResetTrust();p.ChangeSource(source);p.Watch(7);p.ConfirmSnapshot(7,46);Check(p.Matches(7,46),"successful native resource reset permits fresh proofs");
  var gate=new GlassRebuildGate();
  Check(!gate.CanPublish(0)&&!gate.CanPublish(-1),"no token authorizes an unbegun model");
  Check(!gate.TryComplete(0,0)&&gate.Blocked,"default token cannot complete an unbegun model");
  long epoch=gate.Begin();
  Check(gate.CanPublish(epoch)&&gate.Blocked,"fresh partial model is publishable while inventory completion remains blocked");
  Check(!gate.TryComplete(epoch,1),"unresolved work blocks inventory completion");
  Check(gate.CanPublish(epoch),"unresolved work affects completion but does not revoke a current subset proof");
  Check(gate.Blocked,"partial rebuild retains incomplete inventory diagnostics");
  gate.Invalidate(); Check(!gate.TryComplete(epoch,0)&&!gate.CanPublish(epoch),"late callback invalidates completed-looking rebuild");
  long obsolete=epoch;
  epoch=gate.Begin(); Check(gate.TryComplete(epoch,0)&&!gate.Blocked,"all work settled clears incomplete inventory diagnostics");
  Check(!gate.TryComplete(obsolete,0)&&!gate.CanPublish(obsolete)&&gate.CanPublish(epoch),"old completion cannot authorize a new fresh model");
  gate.Invalidate(); Check(gate.Blocked,"remove/reset immediately blocks all old geometry");
  epoch=gate.Begin(); Check(gate.TryComplete(epoch,0),"abandoned unready entries do not block forever");
  Console.WriteLine($"PASS {checks} lifecycle checks");
 }
}
