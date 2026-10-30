using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace DongleDeck {
    public sealed class ReceiverWatcher : IDisposable {
        readonly CancellationTokenSource stop=new CancellationTokenSource();
        readonly FileStream stream;
        readonly LogitechLink logitech;
        public ReceiverWatcher(ReceiverInfo receiver,Action<DeviceSignal> onSignal) {
            if(receiver.IsLogitech) {
                logitech=new LogitechLink(receiver);
                Task.Run(async delegate {
                    byte[] previous=null;
                    try {
                        while(!stop.IsCancellationRequested) {
                            var counters=LogitechProtocol.Request(logitech,0x83,0xb3);
                            if(previous!=null) for(int slot=1;slot<=6;slot++) if(counters[slot+3]!=previous[slot+3]) onSignal(new DeviceSignal {Slot=slot,Online=true,AtUtc=DateTime.UtcNow});
                            previous=counters;
                            byte[] report;while(logitech.TryNotification(out report)) {var signal=DeviceSignal.DecodeLogitech(report);if(signal!=null) onSignal(signal);}
                            await Task.Delay(750,stop.Token).ConfigureAwait(false);
                        }
                    } catch(OperationCanceledException) {} catch(ObjectDisposedException) {} catch(IOException ex) {AppLog.Raw("Unifying activity: "+ex.Message);}
                });
                return;
            }
            stream=HidLink.OpenInput(receiver);
            Task.Run(async delegate {
                try {
                    while(!stop.IsCancellationRequested) {
                        var report=new byte[32];int count=await stream.ReadAsync(report,0,32,stop.Token).ConfigureAwait(false);
                        if(count==0) break;
                        if(count==32) {var signal=DeviceSignal.Decode(report);if(signal!=null) onSignal(signal);}
                    }
                } catch(OperationCanceledException) {} catch(ObjectDisposedException) {} catch(IOException ex) {AppLog.Raw("Activity listener closed: "+ex.Message);}
            });
        }
        public void Dispose() {stop.Cancel();if(stream!=null) stream.Dispose();if(logitech!=null) logitech.Dispose();}
    }
    public sealed class RawActivityMonitor : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Registration {public ushort Page,Usage;public uint Flags;public IntPtr Window;}
        [StructLayout(LayoutKind.Sequential)] struct Header {public uint Type,Size;public IntPtr Device,Parameter;}
        [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterRawInputDevices(Registration[] devices,uint count,uint size);
        [DllImport("user32.dll",SetLastError=true)] static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr device,uint command,StringBuilder data,ref uint size);
        readonly HwndSource source;
        readonly Action<string,byte> onActivity;
        readonly System.Collections.Generic.Dictionary<IntPtr,string> paths=new System.Collections.Generic.Dictionary<IntPtr,string>();
        public RawActivityMonitor(Window window,Action<string,byte> activity) {
            onActivity=activity;source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if(source==null) return;
            source.AddHook(Hook);
            var devices=new[]{new Registration {Page=1,Usage=2,Flags=0x100,Window=source.Handle},new Registration {Page=1,Usage=6,Flags=0x100,Window=source.Handle}};
            if(!RegisterRawInputDevices(devices,(uint)devices.Length,(uint)Marshal.SizeOf(typeof(Registration)))) AppLog.Raw("Raw input registration unavailable");
        }
        IntPtr Hook(IntPtr hwnd,int message,IntPtr parameter,IntPtr data,ref bool handled) {
            if(message==0x219) paths.Clear();
            if(message!=0xff) return IntPtr.Zero;
            uint size=(uint)Marshal.SizeOf(typeof(Header));IntPtr buffer=Marshal.AllocHGlobal((int)size);
            try {
                // Read only the routing header. Keyboard codes and mouse payloads are never read.
                if(GetRawInputData(data,0x10000005,buffer,ref size,(uint)Marshal.SizeOf(typeof(Header)))==uint.MaxValue) return IntPtr.Zero;
                var header=(Header)Marshal.PtrToStructure(buffer,typeof(Header));if(header.Type>1 || header.Device==IntPtr.Zero) return IntPtr.Zero;
                string physical;
                if(!paths.TryGetValue(header.Device,out physical)) {
                    uint count=0;GetRawInputDeviceInfoW(header.Device,0x20000007,null,ref count);
                    var name=new StringBuilder((int)count+1);GetRawInputDeviceInfoW(header.Device,0x20000007,name,ref count);
                    physical=NativeHid.SupportedPath(name.ToString())?NativeHid.PhysicalUsbId(name.ToString()):null;
                    paths[header.Device]=physical;
                }
                if(physical!=null) onActivity(physical,header.Type==1?(byte)1:(byte)2);
            } finally {Marshal.FreeHGlobal(buffer);}
            return IntPtr.Zero;
        }
        public void Dispose() {
            if(source!=null) source.RemoveHook(Hook);
            var devices=new[]{new Registration {Page=1,Usage=2,Flags=1},new Registration {Page=1,Usage=6,Flags=1}};
            RegisterRawInputDevices(devices,(uint)devices.Length,(uint)Marshal.SizeOf(typeof(Registration)));
        }
    }
    public sealed class DeviceChangeMonitor : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Filter {public int Size,Type,Reserved;public Guid ClassGuid;public short Name;}
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient,ref Filter filter,uint flags);
        [DllImport("user32.dll")] static extern bool UnregisterDeviceNotification(IntPtr notification);
        readonly HwndSource source;readonly IntPtr registration;readonly Action<string,bool> changed;
        public DeviceChangeMonitor(Window window,Action<string,bool> onChanged) {
            changed=onChanged;source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            var filter=new Filter {Size=Marshal.SizeOf(typeof(Filter)),Type=5,ClassGuid=NativeHid.ClassGuid};
            registration=RegisterDeviceNotificationW(source.Handle,ref filter,0);
            if(registration==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            source.AddHook(Hook);
        }
        IntPtr Hook(IntPtr hwnd,int message,IntPtr parameter,IntPtr data,ref bool handled) {
            if(message!=0x219) return IntPtr.Zero;
            int kind=parameter.ToInt32();
            if((kind==0x8000 || kind==0x8004) && data!=IntPtr.Zero && Marshal.ReadInt32(data,4)==5) {
                string path=Marshal.PtrToStringUni(IntPtr.Add(data,28));
                if(path!=null && NativeHid.SupportedPath(path)) changed(path,kind==0x8000);
            }
            return IntPtr.Zero;
        }
        public void Dispose() {source.RemoveHook(Hook);if(registration!=IntPtr.Zero) UnregisterDeviceNotification(registration);}
    }
}
