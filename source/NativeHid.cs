using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DongleDeck {
    public sealed class ReceiverInfo {
        public string Key, ManagementPath, InputPath, UsbId, Revision, Label, PhysicalDeviceId;
        public override string ToString() { return Label; }
    }
    internal sealed class HidCollection {
        internal string Path, Key, UsbId, Revision;
        internal ushort Page, Usage, InputBytes, FeatureBytes;
        internal List<int> FeatureIds = new List<int>();
        internal List<int> InputIds = new List<int>();
    }
    internal static class NativeHid {
        [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Guid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)] struct Caps {
            public ushort Usage, Page, InputBytes, OutputBytes, FeatureBytes;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
            public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid guid,IntPtr enumerator,IntPtr parent,uint flags);
        [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr data,ref Guid guid,uint index,ref InterfaceData detail);
        [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,ref InterfaceData data,IntPtr detail,uint length,out uint needed,IntPtr device);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("hid.dll",SetLastError=true)] static extern bool HidD_GetAttributes(SafeFileHandle handle,ref Attributes attributes);
        [DllImport("hid.dll",SetLastError=true)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data,out Caps caps);
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type,IntPtr caps,ref ushort count,IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type,IntPtr caps,ref ushort count,IntPtr data);
        [DllImport("hid.dll",SetLastError=true)] internal static extern bool HidD_SetFeature(SafeFileHandle handle,byte[] report,int length);
        [DllImport("hid.dll",SetLastError=true)] internal static extern bool HidD_GetFeature(SafeFileHandle handle,byte[] report,int length);
        [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint node,string id,uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent,uint node,uint flags);
        [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_IDW(uint node,System.Text.StringBuilder buffer,uint length,uint flags);
        internal static string PhysicalUsbId(string path) {
            string[] parts=path.Split('#');if(parts.Length<3) return null;
            string instance="HID\\"+parts[1]+"\\"+parts[2];uint node;
            if(CM_Locate_DevNodeW(out node,instance,0)!=0) return null;
            for(int i=0;i<8;i++) {
                var buffer=new System.Text.StringBuilder(512);
                if(CM_Get_Device_IDW(node,buffer,512,0)!=0) return null;
                string id=buffer.ToString().ToUpperInvariant();
                if(id.StartsWith("USB\\VID_413C&PID_301C\\",StringComparison.Ordinal)) return id;
                uint parent;if(CM_Get_Parent(out parent,node,0)!=0) return null;node=parent;
            }
            return null;
        }

        static List<int> ReportIds(IntPtr data,int type,ushort values,ushort buttons) {
            var ids=new SortedSet<int>();
            for(int mode=0;mode<2;mode++) {
                ushort count=mode==0?values:buttons; if(count==0) continue;
                IntPtr buffer=Marshal.AllocHGlobal(count*72);
                try {
                    int status=mode==0?HidP_GetValueCaps(type,buffer,ref count,data):HidP_GetButtonCaps(type,buffer,ref count,data);
                    if(status>=0) for(int i=0;i<count;i++) ids.Add(Marshal.ReadByte(buffer,i*72+2));
                } finally {Marshal.FreeHGlobal(buffer);}
            }
            return ids.ToList();
        }
        static string PhysicalKey(string path) {
            string[] parts=path.ToLowerInvariant().Split('#');
            if(parts.Length<3) throw new InvalidOperationException("Unrecognized HID device path.");
            return Regex.Replace(parts[1],"&col[0-9]+$","")+"#"+Regex.Replace(parts[2],"&[0-9a-f]{4}$","");
        }
        public static List<ReceiverInfo> Enumerate() {
            var collections=new List<HidCollection>(); Guid guid; HidD_GetHidGuid(out guid);
            IntPtr set=SetupDiGetClassDevsW(ref guid,IntPtr.Zero,IntPtr.Zero,0x12);
            if(set==new IntPtr(-1)) throw new Win32Exception();
            try {
                for(uint i=0;;i++) {
                    var info=new InterfaceData {Size=Marshal.SizeOf(typeof(InterfaceData))};
                    if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref info)) break;
                    uint needed; SetupDiGetDeviceInterfaceDetailW(set,ref info,IntPtr.Zero,0,out needed,IntPtr.Zero);
                    IntPtr buffer=Marshal.AllocHGlobal((int)needed);
                    try {
                        Marshal.WriteInt32(buffer,IntPtr.Size==8?8:6);
                        if(!SetupDiGetDeviceInterfaceDetailW(set,ref info,buffer,needed,out needed,IntPtr.Zero)) continue;
                        string path=Marshal.PtrToStringUni(IntPtr.Add(buffer,4));
                        // This release targets only the receiver family physically tested here.
                        if(path.IndexOf("vid_413c&pid_301c&mi_02",StringComparison.OrdinalIgnoreCase)<0) continue;
                        using(var handle=CreateFileW(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                            if(handle.IsInvalid) continue;
                            var a=new Attributes {Size=Marshal.SizeOf(typeof(Attributes))};
                            if(!HidD_GetAttributes(handle,ref a) || a.Vendor!=0x413c || a.Product!=0x301c) continue;
                            IntPtr data;
                            if(!HidD_GetPreparsedData(handle,out data)) continue;
                            try {
                                Caps caps; if(HidP_GetCaps(data,out caps)<0) continue;
                                collections.Add(new HidCollection {Path=path,Key=PhysicalKey(path),UsbId=a.Vendor.ToString("X4")+":"+a.Product.ToString("X4"),Revision=a.Version.ToString("X4"),Page=caps.Page,Usage=caps.Usage,InputBytes=caps.InputBytes,FeatureBytes=caps.FeatureBytes,FeatureIds=ReportIds(data,2,caps.FeatureValues,caps.FeatureButtons),InputIds=ReportIds(data,0,caps.InputValues,caps.InputButtons)});
                            } finally {HidD_FreePreparsedData(data);}
                        }
                    } finally {Marshal.FreeHGlobal(buffer);}
                }
            } finally {SetupDiDestroyDeviceInfoList(set);}
            var result=new List<ReceiverInfo>();
            foreach(var group in collections.GroupBy(c=>c.Key).OrderBy(g=>g.Key)) {
                var management=group.Where(c=>c.Page==0xff01 && c.Usage==1 && c.FeatureBytes==32 && c.FeatureIds.SequenceEqual(new[]{8})).ToList();
                var input=group.Where(c=>c.Page==0xff00 && c.Usage==0 && c.InputBytes==32 && c.InputIds.SequenceEqual(new[]{1})).ToList();
                if(management.Count!=1 || input.Count!=1) continue;
                var m=management[0];
                result.Add(new ReceiverInfo {Key=m.Key,ManagementPath=m.Path,InputPath=input[0].Path,UsbId=m.UsbId,Revision=m.Revision,Label="Dell Universal Receiver "+(result.Count+1),PhysicalDeviceId=PhysicalUsbId(m.Path)});
            }
            return result;
        }
    }
    public interface IReceiverLink : IDisposable { byte[] Exchange(params byte[] prefix); }
    internal sealed class HidLink : IReceiverLink {
        readonly SafeFileHandle handle;
        internal HidLink(ReceiverInfo receiver) {
            handle=NativeHid.CreateFileW(receiver.ManagementPath,0xc0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open the receiver. Close other pairing utilities and refresh.");
        }
        public byte[] Exchange(params byte[] prefix) {
            if(prefix.Length<2 || prefix.Length>32 || prefix[0]!=8 || !new byte[]{1,2,3,4,5,6,8,11,16}.Contains(prefix[1])) throw new InvalidOperationException("Unsupported receiver command.");
            byte[] tx=new byte[32];Array.Copy(prefix,tx,prefix.Length);
            if(!NativeHid.HidD_SetFeature(handle,tx,32)) throw new Win32Exception();
            byte[] rx=new byte[32];rx[0]=8;
            if(!NativeHid.HidD_GetFeature(handle,rx,32)) throw new Win32Exception();
            AppLog.Raw("TX "+BitConverter.ToString(prefix)+" RX "+BitConverter.ToString(rx));
            return rx;
        }
        public void Dispose() {handle.Dispose();}
        internal static FileStream OpenInput(ReceiverInfo receiver) {
            var h=NativeHid.CreateFileW(receiver.InputPath,0x80000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(h.IsInvalid) {int error=Marshal.GetLastWin32Error();h.Dispose();throw new Win32Exception(error);}
            return new FileStream(h,FileAccess.Read,32,true);
        }
    }
}
