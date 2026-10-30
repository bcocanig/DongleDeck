using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DongleDeck {
    public sealed class HidppException : IOException {
        public readonly byte Code;
        public HidppException(byte code):base("Unifying receiver error 0x"+code.ToString("X2")+".") {Code=code;}
    }
    // Logitech's public HID++ 1.0 specification: receiver registers 00, 02, B2, B3, B5.
    public static class LogitechProtocol {
        public static byte[] Request(IReceiverLink link,byte command,byte register,params byte[] values) {
            if(values.Length>3) throw new ArgumentException("Short register requests accept three parameters.");
            var tx=new byte[7];tx[0]=0x10;tx[1]=0xff;tx[2]=command;tx[3]=register;Array.Copy(values,0,tx,4,values.Length);
            var rx=link.Exchange(tx);
            if(rx==null || rx.Length<7 || (rx[0]!=0x10 && rx[0]!=0x11) || rx[1]!=0xff) throw new InvalidDataException("Invalid HID++ reply.");
            if(rx[2]==0x8f && rx[3]==command && rx[4]==register) throw new HidppException(rx[5]);
            if(rx[2]!=command || rx[3]!=register || (command==0x83 && (rx[0]!=0x11 || rx.Length!=20 || (register==0xb5 && rx[4]!=tx[4])))) throw new InvalidDataException("Unrelated HID++ reply.");
            return rx;
        }
        public static int Count(IReceiverLink link) {int count=Request(link,0x81,2)[5];if(count>6) throw new InvalidDataException("Invalid Unifying pairing count.");return count;}
        public static List<DeviceSlot> ReadSlots(IReceiverLink link) {
            var slots=new List<DeviceSlot>();
            for(byte slot=1;slot<=6;slot++) {
                byte[] info;
                try {info=Request(link,0x83,0xb5,(byte)(0x20+slot-1));}
                catch(HidppException ex) {if(ex.Code==3 || ex.Code==8) continue;throw;}
                byte kind=info[11];string wpid=info[7].ToString("X2")+info[8].ToString("X2");
                if(kind==0 && (wpid=="0000" || wpid=="FFFF")) continue;
                string id=slot+":"+wpid;
                try {var serial=Request(link,0x83,0xb5,(byte)(0x30+slot-1));string value=BitConverter.ToString(serial,5,4).Replace("-","");if(value!="00000000" && value!="FFFFFFFF") id=value+":"+wpid;} catch(HidppException ex) {if(ex.Code!=2 && ex.Code!=3) throw;}
                string model="WPID "+wpid;
                try {var name=Request(link,0x83,0xb5,(byte)(0x40+slot-1));int length=name[5];if(length>0 && length<=14) {var text=new UTF8Encoding(false,true).GetString(name,6,length).TrimEnd('\0',' ');if(text.Length>0 && !text.Any(char.IsControl)) model=text;}}
                catch(HidppException ex) {if(ex.Code!=2 && ex.Code!=3) throw;}
                slots.Add(new DeviceSlot {Number=slot,Kind=kind,Id=id,Model=model,Brand="Logitech"});
            }
            if(Count(link)!=slots.Count) throw new InvalidDataException("Unifying records are still updating. Refresh the receiver.");
            return slots;
        }
        public static List<DeviceSlot> Remove(IReceiverLink link,DeviceSlot expected) {
            var before=ReadSlots(link);var target=before.SingleOrDefault(s=>s.Number==expected.Number);
            if(target==null || target.Identity!=expected.Identity) throw new InvalidOperationException("That slot has changed. Refresh and select it again.");
            Exception commandError=null;try {Request(link,0x80,0xb2,3,(byte)target.Number,0);} catch(IOException ex) {commandError=ex;}
            List<DeviceSlot> after=null;
            for(int attempt=0;attempt<10;attempt++) {
                try {after=ReadSlots(link);if(!after.Any(s=>s.Identity==target.Identity)) break;} catch(InvalidDataException) {after=null;}
                Thread.Sleep(100);
            }
            if(after==null || after.Any(s=>s.Identity==target.Identity)) throw new IOException("Removal was not verified. Refresh the receiver.",commandError);
            ReceiverProtocol.SameDevices(before.Where(s=>s.Identity!=target.Identity),after);return after;
        }
        public static List<DeviceSlot> Clear(IReceiverLink link,List<DeviceSlot> expected) {
            var remaining=ReadSlots(link);ReceiverProtocol.SameDevices(expected,remaining);
            foreach(var target in expected) {ReceiverProtocol.SameDevices(remaining,ReadSlots(link));remaining=Remove(link,target);}
            return remaining;
        }
        public static DeviceSlot Added(List<DeviceSlot> before,List<DeviceSlot> after) {
            foreach(var prior in before) if(!after.Any(s=>s.Identity==prior.Identity)) throw new InvalidDataException("An existing Unifying pairing is missing. Refresh the receiver.");
            var added=after.Where(s=>!before.Any(p=>p.Identity==s.Identity)).ToList();
            if(added.Count>1) throw new InvalidDataException("More than one pairing changed. Refresh the receiver.");
            return added.SingleOrDefault();
        }
    }
    internal sealed class LogitechLink : IReceiverLink {
        readonly BlockingCollection<byte[]> incoming=new BlockingCollection<byte[]>(256);
        readonly ConcurrentQueue<byte[]> notifications=new ConcurrentQueue<byte[]>();
        readonly CancellationTokenSource stop=new CancellationTokenSource();
        readonly List<FileStream> inputs=new List<FileStream>();
        readonly FileStream output;
        readonly int outputSize;
        readonly object gate=new object();
        Exception readError;bool disposed;
        internal LogitechLink(ReceiverInfo receiver) {
            if(!receiver.IsLogitech || receiver.UsbId!="046D:C52B") throw new InvalidOperationException("Unsupported Logitech receiver.");
            outputSize=receiver.OutputBytes;
            try {
                output=Open(receiver.ManagementPath,FileAccess.Write,receiver.OutputBytes);
                StartInput(receiver.InputPath,receiver.InputBytes);
                if(receiver.LongInputPath!=receiver.InputPath) StartInput(receiver.LongInputPath,receiver.LongInputBytes);
            } catch {Dispose();throw;}
        }
        static FileStream Open(string path,FileAccess access,int size) {
            uint rights=access==FileAccess.Write?0x40000000u:0x80000000u;
            var handle=NativeHid.CreateFileW(path,rights,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(handle.IsInvalid) {int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error,"Cannot open Unifying receiver. Close other pairing utilities.");}
            return new FileStream(handle,access,size,true);
        }
        void StartInput(string path,int size) {
            var stream=Open(path,FileAccess.Read,size);inputs.Add(stream);
            Task.Run(async delegate {
                try {
                    while(!stop.IsCancellationRequested) {
                        var buffer=new byte[size];int count=await stream.ReadAsync(buffer,0,size,stop.Token).ConfigureAwait(false);
                        if(count==0) break;
                        int length=buffer[0]==0x10?7:buffer[0]==0x11?20:0;
                        if(length>0 && count>=length) incoming.TryAdd(buffer.Take(length).ToArray());
                    }
                } catch(OperationCanceledException) {} catch(ObjectDisposedException) {} catch(IOException ex) {readError=ex;}
            });
        }
        internal bool TryNotification(out byte[] report) {return notifications.TryDequeue(out report);}
        void SaveNotification(byte[] report) {
            if(report[2]>=0x80) return;
            notifications.Enqueue(report);byte[] old;while(notifications.Count>128) notifications.TryDequeue(out old);
        }
        public byte[] Exchange(params byte[] prefix) {
            if(prefix.Length!=7 || prefix[0]!=0x10 || prefix[1]!=0xff ||
                !((prefix[2]==0x81 && (prefix[3]==0 || prefix[3]==2)) || (prefix[2]==0x83 && (prefix[3]==0xb5 || prefix[3]==0xb3)) ||
                (prefix[2]==0x80 && (prefix[3]==0 || (prefix[3]==0xb2 && prefix[4]>=1 && prefix[4]<=3))))) throw new InvalidOperationException("Unsupported HID++ command.");
            lock(gate) {
                if(disposed) throw new ObjectDisposedException("LogitechLink");
                var tx=new byte[outputSize];Array.Copy(prefix,tx,7);
                var write=output.WriteAsync(tx,0,tx.Length);
                if(!write.Wait(1500)) {Dispose();throw new IOException("Unifying write timed out.");}
                write.GetAwaiter().GetResult();
                output.Flush();
                var timer=Stopwatch.StartNew();
                while(timer.ElapsedMilliseconds<1800) {
                    if(readError!=null) throw new IOException("Unifying receiver disconnected.",readError);
                    byte[] report;if(!incoming.TryTake(out report,100)) continue;
                    if(report[1]==0xff && ((report[2]==prefix[2] && report[3]==prefix[3] && (prefix[2]!=0x83 || prefix[3]!=0xb5 || report[4]==prefix[4])) || (report[2]==0x8f && report[3]==prefix[2] && report[4]==prefix[3]))) {
                        AppLog.Raw("HID++ TX "+BitConverter.ToString(prefix)+" RX "+BitConverter.ToString(report));return report;
                    }
                    SaveNotification(report);
                }
                Dispose();throw new IOException("Unifying receiver did not reply.");
            }
        }
        public void Dispose() {if(disposed) return;disposed=true;stop.Cancel();foreach(var input in inputs) input.Dispose();if(output!=null) output.Dispose();}
    }
    internal static class LogitechOperations {
        internal static LogitechLink OpenFresh(ReceiverInfo receiver) {
            var fresh=NativeHid.Enumerate().SingleOrDefault(r=>r.IsLogitech && r.Key==receiver.Key);
            if(fresh==null) throw new IOException("The selected receiver was unplugged.");return new LogitechLink(fresh);
        }
        internal static async Task<DeviceSlot> Pair(ReceiverInfo receiver,IProgress<PairProgress> progress,CancellationToken stop) {
            using(var link=OpenFresh(receiver)) {
                var before=LogitechProtocol.ReadSlots(link);if(before.Count>=6) throw new InvalidOperationException("All six slots are occupied.");
                byte[] flags=LogitechProtocol.Request(link,0x81,0);bool changed=(flags[5]&9)!=9;bool opened=false;
                try {
                    stop.ThrowIfCancellationRequested();
                    if(changed) LogitechProtocol.Request(link,0x80,0,flags[4],(byte)(flags[5]|9),flags[6]);
                    LogitechProtocol.Request(link,0x80,0xb2,1,0,30);opened=true;
                    var timer=Stopwatch.StartNew();DateTime nextCount=DateTime.UtcNow;bool saving=false;
                    while(timer.Elapsed.TotalSeconds<34) {
                        if(!saving) stop.ThrowIfCancellationRequested();
                        byte[] report;
                        while(link.TryNotification(out report)) if(report[1]==0xff && report[2]==0x4a && report[4]!=0 && report[4]!=1) throw new IOException("Unifying pairing failed ("+report[4]+ ").");
                        if(DateTime.UtcNow>=nextCount) {
                            if(LogitechProtocol.Count(link)>before.Count) {
                                saving=true;progress.Report(new PairProgress {Message="Checking pairing…",Selecting=true});
                                try {var added=LogitechProtocol.Added(before,LogitechProtocol.ReadSlots(link));if(added!=null) return added;}
                                catch(InvalidDataException) {if(timer.Elapsed.TotalSeconds>=33) throw;}
                            } else progress.Report(new PairProgress {Message="Waiting for a keyboard or mouse…",SecondsLeft=Math.Max(0,30-(int)timer.Elapsed.TotalSeconds)});
                            nextCount=DateTime.UtcNow.AddMilliseconds(500);
                        }
                        await Task.Delay(80).ConfigureAwait(false);
                    }
                    return null;
                } finally {
                    if(opened) try {LogitechProtocol.Request(link,0x80,0xb2,2,0,0);} catch(Exception ex) {AppLog.Raw("Close Unifying pairing: "+ex.Message);}
                    if(changed) try {LogitechProtocol.Request(link,0x80,0,flags[4],flags[5],flags[6]);} catch(Exception ex) {AppLog.Raw("Restore Unifying notifications: "+ex.Message);}
                }
            }
        }
    }
}
