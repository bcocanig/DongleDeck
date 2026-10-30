using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DongleDeck {
    internal sealed class FakeLogitech : IReceiverLink {
        internal readonly List<DeviceSlot> Slots=new List<DeviceSlot>();
        internal readonly List<int> Removed=new List<int>();
        internal bool LostAck,FailRemoval,WrongSelector;
        internal static FakeLogitech Two() {
            var link=new FakeLogitech();
            link.Slots.Add(new DeviceSlot {Number=1,Kind=2,Id="01020304:4002",Model="M510",Brand="Logitech"});
            link.Slots.Add(new DeviceSlot {Number=4,Kind=1,Id="05060708:200F",Model="K520",Brand="Logitech"});
            return link;
        }
        public byte[] Exchange(params byte[] tx) {
            if(tx.Length!=7 || tx[0]!=0x10 || tx[1]!=0xff) throw new Exception("Bad HID++ framing");
            var rx=new byte[tx[2]==0x83?20:7];rx[0]=(byte)(rx.Length==20?0x11:0x10);rx[1]=0xff;rx[2]=tx[2];rx[3]=tx[3];
            if(tx[2]==0x81 && tx[3]==2) rx[5]=(byte)Slots.Count;
            else if(tx[2]==0x83 && tx[3]==0xb5) {
                rx[4]=WrongSelector?(byte)(tx[4]+1):tx[4];int slot=(tx[4]&15)+1;
                var device=Slots.SingleOrDefault(s=>s.Number==slot);
                if(device==null) return new byte[]{0x10,0xff,0x8f,tx[2],tx[3],3,0};
                string[] id=device.Id.Split(':');
                if((tx[4]&0xf0)==0x20) {rx[7]=Convert.ToByte(id[1].Substring(0,2),16);rx[8]=Convert.ToByte(id[1].Substring(2,2),16);rx[11]=device.Kind;}
                else if((tx[4]&0xf0)==0x30) for(int i=0;i<4;i++) rx[5+i]=Convert.ToByte(id[0].Substring(i*2,2),16);
                else if((tx[4]&0xf0)==0x40) {byte[] name=Encoding.UTF8.GetBytes(device.Model);rx[5]=(byte)name.Length;Array.Copy(name,0,rx,6,name.Length);}
                else throw new Exception("Unexpected B5 sub-register");
            } else if(tx[2]==0x80 && tx[3]==0xb2 && tx[4]==3) {
                Removed.Add(tx[5]);if(!FailRemoval) Slots.RemoveAll(s=>s.Number==tx[5]);
                if(LostAck) throw new IOException("Acknowledgement lost");
            } else throw new Exception("Unexpected HID++ command");
            return rx;
        }
        public void Dispose() {}
    }
    internal static class LogitechTests {
        static void Check(bool value) {if(!value) throw new Exception("Unifying check failed");}
        static void Reject(Action action) {try {action();} catch(IOException) {return;} catch(InvalidDataException) {return;} catch(InvalidOperationException) {return;}throw new Exception("Expected rejection");}
        internal static void Run(Action<string,Action> test) {
            test("Unifying parses occupied nonconsecutive slots and model names",delegate {var slots=LogitechProtocol.ReadSlots(FakeLogitech.Two());Check(slots.Count==2 && slots[0].Number==1 && slots[1].Number==4 && slots[1].Model=="K520" && slots[1].Id=="05060708:200F");});
            test("Unifying rejects a reply for another sub-register",delegate {var fake=FakeLogitech.Two();fake.WrongSelector=true;Reject(()=>LogitechProtocol.ReadSlots(fake));});
            test("Unifying stale slot is not removed",delegate {var fake=FakeLogitech.Two();var stale=LogitechProtocol.ReadSlots(fake)[0];fake.Slots[0].Id="AABBCCDD:4002";Reject(()=>LogitechProtocol.Remove(fake,stale));Check(fake.Removed.Count==0);});
            test("Unifying removal preserves the other device",delegate {var fake=FakeLogitech.Two();var target=LogitechProtocol.ReadSlots(fake)[0];var after=LogitechProtocol.Remove(fake,target);Check(after.Count==1 && after[0].Number==4 && fake.Removed.SequenceEqual(new[]{1}));});
            test("Unifying resolves a lost removal acknowledgement by readback",delegate {var fake=FakeLogitech.Two();fake.LostAck=true;Check(LogitechProtocol.Remove(fake,LogitechProtocol.ReadSlots(fake)[0]).Count==1 && fake.Removed.Count==1);});
            test("Unifying does not resend an unverified removal",delegate {var fake=FakeLogitech.Two();fake.FailRemoval=true;Reject(()=>LogitechProtocol.Remove(fake,LogitechProtocol.ReadSlots(fake)[0]));Check(fake.Removed.Count==1);});
            test("Unifying clear handles gaps between occupied slots",delegate {var fake=FakeLogitech.Two();Check(LogitechProtocol.Clear(fake,LogitechProtocol.ReadSlots(fake)).Count==0 && fake.Removed.SequenceEqual(new[]{1,4}));});
            test("Unifying pairing verification detects a replaced prior device",delegate {var fake=FakeLogitech.Two();var before=LogitechProtocol.ReadSlots(fake);fake.Slots[0].Id="AABBCCDD:4002";Reject(()=>LogitechProtocol.Added(before,LogitechProtocol.ReadSlots(fake)));});
            test("Unifying verifies a new pairing while preserving stored devices",delegate {var fake=FakeLogitech.Two();var before=LogitechProtocol.ReadSlots(fake);fake.Slots.Add(new DeviceSlot {Number=2,Kind=1,Id="090A0B0C:200F",Model="K520",Brand="Logitech"});var added=LogitechProtocol.Added(before,LogitechProtocol.ReadSlots(fake));Check(added!=null && added.Number==2);});
            test("Unifying slot report can explicitly mean offline",delegate {var signal=DeviceSignal.DecodeLogitech(new byte[]{0x10,4,0x41,4,0x41,0x0f,0x20});Check(signal!=null && signal.Slot==4 && signal.Online==false);});
            test("Unifying reconnect identifies the exact slot",delegate {var signal=DeviceSignal.DecodeLogitech(new byte[]{0x10,1,0x41,4,0x22,2,0x40});Check(signal!=null && signal.Slot==1 && signal.Online==true);});
        }
    }
}
