using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DongleDeck {
    public sealed class DeviceSignal {
        public int Slot; public bool? Online; public int? Battery; public DateTime AtUtc;
        public static DeviceSignal DecodeLogitech(byte[] report) {
            if(report==null || report.Length<7 || report[0]!=0x10 || report[1]<1 || report[1]>6) return null;
            if(report[2]==0x40 && report[3]==2) return new DeviceSignal {Slot=report[1],Online=false,AtUtc=DateTime.UtcNow};
            if(report[2]==0x41 && (report[3]&7)==4) return new DeviceSignal {Slot=report[1],Online=(report[4]&0x40)==0,AtUtc=DateTime.UtcNow};
            return null;
        }
        public static DeviceSignal Decode(byte[] report) {
            if(report==null || report.Length!=32 || report[0]!=1 || report[2]<1 || report[2]>6) return null;
            var signal=new DeviceSignal {Slot=report[2],AtUtc=DateTime.UtcNow};
            if(report[1]==6 && report[3]<=1) signal.Online=report[3]==1;
            else if(report[1]==8 && report[3]<=100) signal.Battery=report[3];
            else return null;
            return signal;
        }
    }
    public sealed class DeviceSlot : INotifyPropertyChanged {
        public int Number {get;set;}
        public byte Kind {get;set;}
        public string Id {get;set;}
        public string Model {get;set;}
        public string Brand="Dell";
        public string DeviceType {get {return Kind==1?"Keyboard":Kind==2?"Mouse":Kind==3?"Numpad":Kind==8?"Trackball":Kind==9?"Touchpad":"Device";}}
        public string Title {get {return Brand+" "+DeviceType.ToLowerInvariant();}}
        public string Detail {get {return Model+"  ·  Slot "+Number;}}
        public string Identity {get {return Kind+":"+Id+":"+Model;}}
        public byte Capabilities {get;set;}
        public int? BatteryPercent {get;set;}
        public bool? ReportedOnline {get;set;}
        public DateTime LastSeenUtc {get;set;}
        public DateTime LastInputUtc {get;set;}
        public string BatteryText {get {return BatteryPercent.HasValue?"Battery "+BatteryPercent.Value+"%":"Battery unknown";}}
        public string ActivityText {get {return ActivityAt(DateTime.UtcNow);}}
        public string ActivityAt(DateTime now) {
            if(ReportedOnline==false || LastSeenUtc==DateTime.MinValue) return "Inactive";
            if((now-LastInputUtc).TotalSeconds<2) return "Active";
            var age=now-LastSeenUtc;
            if(age.TotalSeconds<2) return "Active";
            if(age.TotalSeconds<60) return "Seen "+(int)age.TotalSeconds+"s ago";
            return "Seen "+(int)age.TotalMinutes+"m ago";
        }
        public static bool DarkTheme {get;set;}
        public string ActivityColor {get {bool active=ReportedOnline!=false && (DateTime.UtcNow-LastSeenUtc).TotalSeconds<2;return DarkTheme?(active?"#7AE1AA":"#A6B5C8"):(active?"#216D43":"#5E7088");}}
        public event PropertyChangedEventHandler PropertyChanged;
        public void NotifyTelemetry() {var changed=PropertyChanged;if(changed!=null) foreach(string name in new[]{"ActivityText","ActivityColor","BatteryText"}) changed(this,new PropertyChangedEventArgs(name));}
        public void CopyTelemetry(DeviceSlot old) {ReportedOnline=old.ReportedOnline;LastSeenUtc=old.LastSeenUtc;LastInputUtc=old.LastInputUtc;if(!BatteryPercent.HasValue) BatteryPercent=old.BatteryPercent;}
    }
    public sealed class ReceiverSnapshot {
        public ReceiverInfo Receiver;
        public List<DeviceSlot> Slots;
    }
    public sealed class Candidate {
        public byte Kind; public string Id,Model; public bool AlreadyPaired;
        public string Title {get {return Model;}}
        public string Detail {get {return AlreadyPaired?"Already paired to this receiver":"Ready to pair";}}
        public bool CanPair {get {return !AlreadyPaired;}}
    }
    public sealed class PairProgress {
        public string Message; public int SecondsLeft; public bool Selecting;
        public Candidate Candidate;
    }
    public static class AppLog {
        static readonly object Gate=new object();
        public static readonly string DirectoryPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DongleDeck");
        public static readonly string PathName=Path.Combine(DirectoryPath,"dongledeck.log");
        public static void Raw(string text) {
            try {lock(Gate) {Directory.CreateDirectory(DirectoryPath);File.AppendAllText(PathName,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+text+Environment.NewLine);}} catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
    }
    public enum DellServiceState {Missing,Stopped,Running,Unknown}
    public sealed class DellServiceStatus {
        public DellServiceState State;
        public bool Administrator;
        public bool BlocksDell {get {return !Administrator && (State==DellServiceState.Running || State==DellServiceState.Unknown);}}
        public string Title {get {
            if(State==DellServiceState.Missing) return "DDPM not found";
            if(State==DellServiceState.Stopped) return "DDPM found · service stopped";
            if(State==DellServiceState.Unknown) return "DDPM service status unavailable";
            return Administrator?"DDPM found · admin mode":"DDPM found · Dell receiver access blocked";
        }}
        public string Detail {get {return BlocksDell?"Close DongleDeck, then right-click DongleDeck.exe → Run as administrator to stop the service temporarily.":"";}}
        public static bool IsAdministrator {get {using(var identity=WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
        public static DellServiceStatus Read() {
            var result=new DellServiceStatus {Administrator=IsAdministrator};
            try {using(var service=new ServiceController("DPMService")) result.State=service.Status==ServiceControllerStatus.Stopped?DellServiceState.Stopped:DellServiceState.Running;}
            catch(Exception ex) {
                var native=ex as Win32Exception ?? ex.InnerException as Win32Exception;
                result.State=native!=null && native.NativeErrorCode==1060?DellServiceState.Missing:DellServiceState.Unknown;
                if(result.State==DellServiceState.Unknown) AppLog.Raw("DDPM service check: "+ex.Message);
            }
            return result;
        }
    }
    public sealed class DellServiceBlockedException : InvalidOperationException {
        public DellServiceBlockedException():base("DDPM is running. Close DongleDeck, then right-click DongleDeck.exe → Run as administrator to use Dell receivers.") {}
    }
    public sealed class ServiceLease : IDisposable {
        readonly ServiceController service; readonly bool restore; bool disposed;
        public ServiceLease() {
            service=new ServiceController("DPMService");
            try {
                var status=service.Status;
                if(status==ServiceControllerStatus.StartPending) {service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15));status=service.Status;}
                if(status==ServiceControllerStatus.StopPending) {service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));status=service.Status;}
                if(status!=ServiceControllerStatus.Stopped && !DellServiceStatus.IsAdministrator) throw new DellServiceBlockedException();
                restore=status==ServiceControllerStatus.Running;
                if(restore) {service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));AppLog.Raw("Dell peripheral service paused");}
            } catch(Exception ex) {
                if(ex is InvalidOperationException && ex.InnerException is System.ComponentModel.Win32Exception && ((System.ComponentModel.Win32Exception)ex.InnerException).NativeErrorCode==1060) {service.Dispose();service=null;return;}
                // A failed stop can still finish afterward, so restore the initial running state.
                if(restore) try {service.Refresh();if(service.Status==ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));if(service.Status==ServiceControllerStatus.Stopped) service.Start();} catch(Exception recovery) {AppLog.Raw("Service recovery: "+recovery.Message);}
                service.Dispose();throw;
            }
        }
        public void Dispose() {
            if(disposed) return;disposed=true;
            if(service==null) return;
            try {
                if(restore) {service.Refresh();if(service.Status==ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(15));if(service.Status==ServiceControllerStatus.Stopped) service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15));AppLog.Raw("Dell peripheral service restored");}
            } finally {service.Dispose();}
        }
    }
    public static class ReceiverProtocol {
        public static string Id(byte[] r,int offset) {return BitConverter.ToString(r,offset,3).Replace("-","");}
        public static string Model(byte[] r,int offset,int count) {
            string model=Encoding.ASCII.GetString(r,offset,count).TrimEnd('\0',' ');
            if(model.Length==0 || model.Any(c=>c<32 || c>126)) throw new InvalidDataException("Receiver returned an invalid device name.");
            return model;
        }
        static void Echo(byte[] r,byte command) {if(r==null || r.Length!=32 || r[0]!=8 || r[1]!=command) throw new InvalidDataException("Unexpected reply from the receiver. Refresh before trying again.");}
        public static List<DeviceSlot> ReadSlots(IReceiverLink link) {
            byte[] info=link.Exchange(8,1);Echo(info,1);
            if(!Encoding.ASCII.GetString(info,6,24).StartsWith("Dell Universal Receiver",StringComparison.Ordinal)) throw new InvalidDataException("This receiver is not supported.");
            var slots=new List<DeviceSlot>();
            for(byte number=1;number<=6;number++) {
                byte[] r=link.Exchange(8,3,number);Echo(r,3);
                if(r[3]==1 || r[3]==2) {
                    if(r[2]!=number) throw new InvalidDataException("Receiver slot numbers do not match.");
                    string id=Id(r,5);if(id=="000000" || id=="FFFFFF") throw new InvalidDataException("Invalid device identifier.");
                    slots.Add(new DeviceSlot {Number=number,Kind=r[3],Id=id,Model=Model(r,8,20),Capabilities=r[30]});
                } else if(r[3]!=0 && r[3]!=255) throw new InvalidDataException("Unknown slot type; no changes were made.");
            }
            byte[] count=link.Exchange(8,2);Echo(count,2);
            if(count[2]!=slots.Count || slots.Select(s=>s.Identity).Distinct().Count()!=slots.Count) throw new InvalidDataException("Pairing records are inconsistent. Refresh before making changes.");
            return slots;
        }
        public static void SameDevices(IEnumerable<DeviceSlot> expected,IEnumerable<DeviceSlot> actual) {
            if(!expected.Select(s=>s.Identity).OrderBy(s=>s).SequenceEqual(actual.Select(s=>s.Identity).OrderBy(s=>s))) throw new InvalidDataException("Pairing records changed unexpectedly. No further commands will be sent; refresh to inspect the receiver.");
        }
        public static List<DeviceSlot> Remove(IReceiverLink link,DeviceSlot expected) {
            var before=ReadSlots(link);
            DeviceSlot current=before.FirstOrDefault(s=>s.Number==expected.Number);
            if(current==null || current.Identity!=expected.Identity) throw new InvalidOperationException("That slot has changed. Refresh and select the device again.");
            Exception commandError=null;
            try {Echo(link.Exchange(8,6,(byte)current.Number),6);} catch(Exception ex) {commandError=ex;}
            // Readback resolves a lost acknowledgement. Never automatically resend a removal.
            List<DeviceSlot> after=null;Exception readError=null;
            // Some receiver revisions update the count and slot records separately.
            // Wait for readback to settle; never resend the removal command.
            for(int attempt=0;attempt<12;attempt++) {
                try {after=ReadSlots(link);if(!after.Any(s=>s.Identity==current.Identity)) break;} catch(InvalidDataException ex) {readError=ex;after=null;}
                Thread.Sleep(100);
            }
            if(after==null) throw new InvalidOperationException("The receiver is still updating its records. Refresh to inspect the result.",readError);
            if(after.Any(s=>s.Identity==current.Identity)) throw new InvalidOperationException("Removal was not verified. Refresh before trying again.",commandError);
            SameDevices(before.Where(s=>s.Identity!=current.Identity),after);
            return after;
        }
        public static void ReadBatteries(IReceiverLink link,List<DeviceSlot> slots) {
            foreach(var slot in slots.Where(s=>(s.Capabilities&0x10)!=0)) {
                try {byte[] r=link.Exchange(8,16,(byte)slot.Number);Echo(r,16);if(r[2]==slot.Number && r[3]<=100) slot.BatteryPercent=r[3];} catch(Exception ex) {AppLog.Raw("Battery unavailable: "+ex.Message);}
            }
        }
        public static List<DeviceSlot> Clear(IReceiverLink link,List<DeviceSlot> confirmed) {
            var remaining=ReadSlots(link);SameDevices(confirmed,remaining);
            foreach(var target in confirmed) {
                var fresh=ReadSlots(link);SameDevices(remaining,fresh);
                DeviceSlot current=fresh.Single(s=>s.Identity==target.Identity);
                remaining=Remove(link,current);
            }
            if(remaining.Count!=0) throw new InvalidDataException("The receiver is not empty; refresh to inspect it.");
            return remaining;
        }
        public static DeviceSlot VerifyPair(IReceiverLink link,List<DeviceSlot> before,Candidate candidate) {
            var after=ReadSlots(link);
            foreach(var prior in before) if(!after.Any(s=>s.Identity==prior.Identity)) throw new InvalidDataException("An existing pairing is missing. Refresh before making another change.");
            DeviceSlot added=after.FirstOrDefault(s=>s.Kind==candidate.Kind && s.Id==candidate.Id);
            if(added!=null) SameDevices(before,after.Where(s=>s.Identity!=added.Identity));
            return added;
        }
    }
    public sealed class PairingSession {
        readonly object gate=new object();Candidate selection;
        public volatile bool Automatic;
        public void Select(Candidate candidate) {lock(gate) {if(selection==null && !candidate.AlreadyPaired) selection=candidate;}}
        public async Task<DeviceSlot> Run(ReceiverInfo receiver,byte kind,IProgress<PairProgress> progress,CancellationToken stop) {
            if(receiver.IsLogitech) return await LogitechOperations.Pair(receiver,progress,stop).ConfigureAwait(false);
            using(var lease=new ServiceLease()) {
                var receivers=NativeHid.Enumerate();ReceiverInfo fresh=receivers.SingleOrDefault(r=>r.Key==receiver.Key);
                if(fresh==null) throw new InvalidOperationException("The selected receiver was unplugged. Refresh the receiver list.");
                using(var link=new HidLink(fresh)) {
                    var before=ReceiverProtocol.ReadSlots(link);
                    if(before.Count>=6) throw new InvalidOperationException("All six slots are occupied. Remove a device before pairing.");
                    stop.ThrowIfCancellationRequested();
                    using(var input=HidLink.OpenInput(fresh))
                    using(var readCancel=new CancellationTokenSource()) {
                        byte[] buffer=new byte[32];Task<int> read=input.ReadAsync(buffer,0,32,readCancel.Token);
                        var found=new Dictionary<string,Candidate>();Candidate selected=null;
                        DateTime start=DateTime.UtcNow,nextPoll=start,selectedAt=start;
                        try {
                            link.Exchange(8,11);
                            while(true) {
                                if(selected==null) stop.ThrowIfCancellationRequested();
                                int left=Math.Max(0,30-(int)(DateTime.UtcNow-start).TotalSeconds);
                                if(selected==null && left==0) return null;
                                if(selected!=null && (DateTime.UtcNow-selectedAt).TotalSeconds>=20) {
                                    var verified=ReceiverProtocol.VerifyPair(link,before,selected);
                                    if(verified!=null) return verified;
                                    throw new InvalidOperationException("Pairing could not be verified. Refresh before trying again.");
                                }
                                if(read.IsCompleted) {
                                    int length=await read.ConfigureAwait(false);
                                    if(length==32 && buffer[0]==1 && buffer[1]==1 && (buffer[2]==1 || buffer[2]==2) && (kind==0 || buffer[2]==kind)) {
                                        string id=ReceiverProtocol.Id(buffer,14);
                                        if(id!="000000" && id!="FFFFFF" && !found.ContainsKey(id)) {
                                            var candidate=new Candidate {Kind=buffer[2],Id=id,Model=ReceiverProtocol.Model(buffer,4,10),AlreadyPaired=before.Any(s=>s.Kind==buffer[2] && s.Id==id)};
                                            found.Add(id,candidate);
                                            progress.Report(new PairProgress {Candidate=candidate,SecondsLeft=left,Message="Device found"});
                                            if(Automatic && !candidate.AlreadyPaired) Select(candidate);
                                        }
                                    }
                                    buffer=new byte[32];read=input.ReadAsync(buffer,0,32,readCancel.Token);
                                }
                                Candidate choice;lock(gate) {choice=selection;}
                                if(choice==null && Automatic) {choice=found.Values.FirstOrDefault(c=>!c.AlreadyPaired);if(choice!=null) Select(choice);}
                                if(selected==null && choice!=null) {
                                    if(!found.ContainsKey(choice.Id) || (kind!=0 && choice.Kind!=kind) || choice.AlreadyPaired) throw new InvalidOperationException("Select a newly discovered device.");
                                    ReceiverProtocol.SameDevices(before,ReceiverProtocol.ReadSlots(link));
                                    selected=found[choice.Id];selectedAt=DateTime.UtcNow;
                                    byte[] id=Enumerable.Range(0,3).Select(i=>Convert.ToByte(selected.Id.Substring(i*2,2),16)).ToArray();
                                    link.Exchange(8,4,1,id[0],id[1],id[2]);
                                    nextPoll=DateTime.UtcNow;
                                    progress.Report(new PairProgress {Message="Pairing "+selected.Model+"…",Selecting=true,SecondsLeft=0});
                                }
                                if(DateTime.UtcNow>=nextPoll) {
                                    if(selected==null) {link.Exchange(8,8);progress.Report(new PairProgress {Message=found.Count==0?"Searching nearby…":"Choose the device below",SecondsLeft=left});}
                                    else {
                                        byte[] status=link.Exchange(8,5);
                                        if(status[0]==8 && status[1]==5 && status[2]==1) {
                                            if(status[4]!=selected.Kind || ReceiverProtocol.Id(status,6)!=selected.Id) throw new InvalidDataException("Unexpected pairing completion. Refresh the receiver.");
                                            progress.Report(new PairProgress {Message="Checking pairing…",Selecting=true,SecondsLeft=0});
                                            var verified=ReceiverProtocol.VerifyPair(link,before,selected);
                                            if(verified==null) throw new InvalidDataException("Pairing finished but the device was not stored. Refresh the receiver.");
                                            return verified;
                                        }
                                    }
                                    nextPoll=DateTime.UtcNow.AddSeconds(1);
                                }
                                await Task.Delay(80).ConfigureAwait(false);
                            }
                        } finally {
                            readCancel.Cancel();
                            try {read.GetAwaiter().GetResult();} catch(OperationCanceledException) {} catch(IOException) {}
                        }
                    }
                }
            }
        }
    }
    public sealed class ReceiverInventory {
        public DellServiceStatus DellService;
        public List<ReceiverSnapshot> Receivers=new List<ReceiverSnapshot>();
    }
    public static class ReceiverOperations {
        public static List<ReceiverSnapshot> Scan() {return Inspect().Receivers;}
        public static ReceiverInventory Inspect() {
            var result=new ReceiverInventory {DellService=DellServiceStatus.Read()};ServiceLease lease=null;
            // Check USB paths before opening HID collections: Dell's service can lock even descriptor access.
            try {
                if(!result.DellService.BlocksDell && NativeHid.DellPresent()) {
                    try {lease=new ServiceLease();}
                    catch(DellServiceBlockedException) {result.DellService=DellServiceStatus.Read();}
                }
                foreach(var receiver in NativeHid.Enumerate().Where(r=>r.IsLogitech || !result.DellService.BlocksDell)) {
                    if(receiver.IsLogitech) using(var link=LogitechOperations.OpenFresh(receiver)) result.Receivers.Add(new ReceiverSnapshot {Receiver=receiver,Slots=LogitechProtocol.ReadSlots(link)});
                    else using(var link=new HidLink(receiver)) {var slots=ReceiverProtocol.ReadSlots(link);ReceiverProtocol.ReadBatteries(link,slots);result.Receivers.Add(new ReceiverSnapshot {Receiver=receiver,Slots=slots});}
                }
            } finally {if(lease!=null) lease.Dispose();}
            return result;
        }
        public static List<DeviceSlot> Remove(ReceiverInfo receiver,DeviceSlot slot) {
            if(receiver.IsLogitech) using(var link=LogitechOperations.OpenFresh(receiver)) return LogitechProtocol.Remove(link,slot);
            using(var lease=new ServiceLease()) using(var link=OpenFresh(receiver)) return ReceiverProtocol.Remove(link,slot);
        }
        public static List<DeviceSlot> Clear(ReceiverInfo receiver,List<DeviceSlot> slots) {
            if(receiver.IsLogitech) using(var link=LogitechOperations.OpenFresh(receiver)) return LogitechProtocol.Clear(link,slots);
            using(var lease=new ServiceLease()) using(var link=OpenFresh(receiver)) return ReceiverProtocol.Clear(link,slots);
        }
        static HidLink OpenFresh(ReceiverInfo receiver) {
            var current=NativeHid.Enumerate().SingleOrDefault(r=>r.Key==receiver.Key);
            if(current==null) throw new InvalidOperationException("The selected receiver was unplugged. Refresh the receiver list.");
            return new HidLink(current);
        }
    }
}
