using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DongleDeck {
    internal sealed class FakeLink : IReceiverLink {
        public List<DeviceSlot> Slots=new List<DeviceSlot>();
        public List<int> RemovalSlots=new List<int>();
        public bool Compact,LostAcknowledgement;
        public int FailRemovalNumber,ChangeOnInfoRead,InfoReads,CountLagAfterRemoval;
        int staleCount,lagLeft;
        public byte[] Exchange(params byte[] prefix) {
            var r=new byte[32];r[0]=8;r[1]=prefix[1];
            if(prefix[1]==1) {
                InfoReads++;
                if(InfoReads==ChangeOnInfoRead) Slots[0].Id="A1B2C3";
                Array.Copy(Encoding.ASCII.GetBytes("Dell Universal Receiver"),0,r,6,23);
            } else if(prefix[1]==2) {r[2]=(byte)(lagLeft>0?staleCount:Slots.Count);if(lagLeft>0)lagLeft--;}
            else if(prefix[1]==3) {
                r[2]=prefix[2];var s=Slots.FirstOrDefault(x=>x.Number==prefix[2]);
                if(s!=null) {r[3]=s.Kind;r[30]=s.Capabilities;for(int i=0;i<3;i++) r[5+i]=Convert.ToByte(s.Id.Substring(i*2,2),16);byte[] model=Encoding.ASCII.GetBytes(s.Model);Array.Copy(model,0,r,8,model.Length);}
            } else if(prefix[1]==6) {
                RemovalSlots.Add(prefix[2]);
                staleCount=Slots.Count;lagLeft=CountLagAfterRemoval;
                if(RemovalSlots.Count!=FailRemovalNumber) Slots.RemoveAll(s=>s.Number==prefix[2]);
                if(Compact) for(int i=0;i<Slots.Count;i++) Slots[i].Number=i+1;
                if(LostAcknowledgement) r[1]=0;
            } else if(prefix[1]==16) {r[2]=prefix[2];r[3]=100;} else throw new Exception("Unexpected test command");
            return r;
        }
        public void Dispose() {}
        public static FakeLink Three() {
            var f=new FakeLink();
            f.Slots.Add(new DeviceSlot {Number=1,Kind=1,Id="010203",Model="KB7120W"});
            f.Slots.Add(new DeviceSlot {Number=2,Kind=2,Id="040506",Model="MS5320W"});
            f.Slots.Add(new DeviceSlot {Number=3,Kind=1,Id="070809",Model="KB7120W"});return f;
        }
    }
    public static class ProtocolTests {
        static int passed;
        static void Check(bool value,string message) {if(!value) throw new Exception(message);}
        static void Reject(Action action) {try {action();} catch(InvalidOperationException) {return;} catch(InvalidDataException) {return;}throw new Exception("Operation should have stopped");}
        static void Test(string name,Action action) {action();passed++;Console.WriteLine("PASS "+name);}
        public static int Main() {
            Test("Remove one device and preserve the others",delegate {var f=FakeLink.Three();var target=ReceiverProtocol.ReadSlots(f)[1];var after=ReceiverProtocol.Remove(f,target);Check(after.Count==2 && after.All(s=>s.Id!="040506") && f.RemovalSlots.SequenceEqual(new[]{2}),"Wrong removal");});
            Test("Stale selection sends no removal",delegate {var f=FakeLink.Three();var target=ReceiverProtocol.ReadSlots(f)[1];target.Id="AABBCC";Reject(()=>ReceiverProtocol.Remove(f,target));Check(f.RemovalSlots.Count==0,"Stale slot was modified");});
            Test("Lost acknowledgement is resolved by readback",delegate {var f=FakeLink.Three();f.LostAcknowledgement=true;var target=ReceiverProtocol.ReadSlots(f)[1];Check(ReceiverProtocol.Remove(f,target).Count==2 && f.RemovalSlots.Count==1,"Removal was retried or not verified");});
            Test("Failed removal is not retried",delegate {var f=FakeLink.Three();f.FailRemovalNumber=1;var target=ReceiverProtocol.ReadSlots(f)[1];Reject(()=>ReceiverProtocol.Remove(f,target));Check(f.RemovalSlots.Count==1 && f.Slots.Count==3,"Unexpected retry");});
            Test("Clear handles shifting slot numbers",delegate {var f=FakeLink.Three();f.Compact=true;var confirmed=ReceiverProtocol.ReadSlots(f);Check(ReceiverProtocol.Clear(f,confirmed).Count==0 && f.RemovalSlots.SequenceEqual(new[]{1,1,1}),"Wrong compacted slot removed");});
            Test("Clear stops after a partial failure",delegate {var f=FakeLink.Three();f.FailRemovalNumber=2;var confirmed=ReceiverProtocol.ReadSlots(f);Reject(()=>ReceiverProtocol.Clear(f,confirmed));Check(f.RemovalSlots.Count==2 && f.Slots.Count==2,"Clear continued after failure");});
            Test("Clear rejects a changed confirmation snapshot",delegate {var f=FakeLink.Three();var confirmed=ReceiverProtocol.ReadSlots(f);f.Slots[0].Id="AABBCC";Reject(()=>ReceiverProtocol.Clear(f,confirmed));Check(f.RemovalSlots.Count==0,"Changed snapshot was cleared");});
            Test("Clear rechecks between removals",delegate {var f=FakeLink.Three();var confirmed=ReceiverProtocol.ReadSlots(f);f.ChangeOnInfoRead=3;Reject(()=>ReceiverProtocol.Clear(f,confirmed));Check(f.RemovalSlots.Count==0,"Changed device was removed");});
            Test("Pair readback verifies the new mouse",delegate {var f=FakeLink.Three();var before=ReceiverProtocol.ReadSlots(f);f.Slots.Add(new DeviceSlot {Number=4,Kind=2,Id="AABBCC",Model="MS5320W"});var result=ReceiverProtocol.VerifyPair(f,before,new Candidate {Kind=2,Id="AABBCC",Model="MS5320W"});Check(result!=null && result.Number==4,"New mouse missing");});
            Test("Pair verification detects loss of an existing device",delegate {var f=FakeLink.Three();var before=ReceiverProtocol.ReadSlots(f);f.Slots.RemoveAt(0);f.Slots.Add(new DeviceSlot {Number=4,Kind=2,Id="AABBCC",Model="MS5320W"});Reject(()=>ReceiverProtocol.VerifyPair(f,before,new Candidate {Kind=2,Id="AABBCC",Model="MS5320W"}));});
            Test("Empty receiver can be cleared without mutations",delegate {var f=new FakeLink();Check(ReceiverProtocol.Clear(f,new List<DeviceSlot>()).Count==0 && f.RemovalSlots.Count==0,"Empty clear sent a mutation");});
            Test("Removal waits for delayed receiver count without retrying",delegate {var f=FakeLink.Three();var target=ReceiverProtocol.ReadSlots(f)[1];f.CountLagAfterRemoval=3;Check(ReceiverProtocol.Remove(f,target).Count==2 && f.RemovalSlots.Count==1,"Delayed readback failed");});
            Test("Power-on notification identifies its exact slot",delegate {var r=new byte[32];r[0]=1;r[1]=6;r[2]=4;r[3]=1;var s=DeviceSignal.Decode(r);Check(s!=null && s.Slot==4 && s.Online==true,"Power-on decode failed");});
            Test("Power-off notification is not treated as connected",delegate {var r=new byte[32];r[0]=1;r[1]=6;r[2]=1;r[3]=0;var s=DeviceSignal.Decode(r);Check(s.Online==false,"Power-off decode failed");});
            Test("Unrecognized notification cannot mark a device connected",delegate {var r=new byte[32];r[0]=1;r[1]=0x26;r[2]=1;r[3]=1;Check(DeviceSignal.Decode(r)==null,"Unknown report was interpreted");});
            Test("Battery report carries charge without asserting connection",delegate {var r=new byte[32];r[0]=1;r[1]=8;r[2]=1;r[3]=100;var s=DeviceSignal.Decode(r);Check(s.Battery==100 && s.Online==null,"Battery was treated as connection proof");});
            Test("Cached battery does not make an inactive device active",delegate {var f=FakeLink.Three();f.Slots[0].Capabilities=0x10;var slots=ReceiverProtocol.ReadSlots(f);ReceiverProtocol.ReadBatteries(f,slots);Check(slots[0].BatteryPercent==100 && slots[0].ActivityText=="Inactive","Cached battery marked device active");});
            Test("Activity shows Active during the first two seconds",delegate {var now=DateTime.UtcNow;var slot=new DeviceSlot {LastSeenUtc=now.AddSeconds(-1),LastInputUtc=now.AddSeconds(-1),ReportedOnline=true};Check(slot.ActivityAt(now)=="Active","Active cutoff incorrect");});
            Test("Seen counter starts at two seconds",delegate {var now=DateTime.UtcNow;var slot=new DeviceSlot {LastSeenUtc=now.AddSeconds(-2),LastInputUtc=now.AddSeconds(-2),ReportedOnline=true};Check(slot.ActivityAt(now)=="Seen 2s ago","Seen counter delayed");});
            Test("Idle status retains the exact seconds seen",delegate {var now=DateTime.UtcNow;var slot=new DeviceSlot {LastSeenUtc=now.AddSeconds(-7),LastInputUtc=now.AddSeconds(-7),ReportedOnline=true};Check(slot.ActivityAt(now)=="Seen 7s ago","Seen seconds incorrect");});
            Test("Disconnected device is labelled Inactive",delegate {var now=DateTime.UtcNow;var slot=new DeviceSlot {LastSeenUtc=now,LastInputUtc=now,ReportedOnline=false};Check(slot.ActivityAt(now)=="Inactive","Inactive label incorrect");});
            LogitechTests.Run(Test);
            Test("Running DDPM blocks Dell for a standard user",delegate {var status=new DellServiceStatus {State=DellServiceState.Running};Check(status.BlocksDell && status.Detail.Contains("Run as administrator"),"Missing service conflict guidance");});
            Test("No DDPM service needs no elevation",delegate {var status=new DellServiceStatus {State=DellServiceState.Missing};Check(!status.BlocksDell && status.Detail.Length==0,"Missing service blocks receiver access");});
            Test("Stopped DDPM service needs no elevation",delegate {var status=new DellServiceStatus {State=DellServiceState.Stopped};Check(!status.BlocksDell && status.Detail.Length==0,"Stopped service blocks receiver access");});
            Test("Admin launch can manage running DDPM",delegate {var status=new DellServiceStatus {State=DellServiceState.Running,Administrator=true};Check(!status.BlocksDell && status.Detail.Length==0,"Admin launch incorrectly blocked");});
            Test("Unknown DDPM status cannot trigger a standard-user service stop",delegate {var status=new DellServiceStatus {State=DellServiceState.Unknown};Check(status.BlocksDell,"Unknown service status treated as stopped");});
            Console.WriteLine(passed+" protocol checks passed.");return 0;
        }
    }
}
