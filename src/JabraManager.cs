using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jabra.NET.Sdk.Core;
using Jabra.NET.Sdk.Core.Types;
using Jabra.NET.Sdk.DevicePairing;
using JabraBtStatus = Jabra.NET.Sdk.DevicePairing.BluetoothConnectionStatus;

namespace DongleDeck {
    public static class JabraClassifier {
        static readonly string[] KnownOuis = new[] {
            "00:1A:45", "1C:48:F9", "30:50:75", "50:1A:A5",
            "70:BF:92", "74:5C:4B", "D0:8A:55", "00:23:7A",
            "50:C2:ED", "08:DF:1F"
        };

        public static string NormalizeBluetoothAddress(string raw) {
            if(string.IsNullOrWhiteSpace(raw)) return "";
            var hex = new string(raw.Where(c => Uri.IsHexDigit(c)).ToArray()).ToUpperInvariant();
            if(hex.Length == 12) {
                return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
            }
            return raw.Trim().ToUpperInvariant();
        }

        public static string FormatBluetoothAddress(ulong address) {
            if(address == 0) return "";
            byte[] bytes = new byte[6];
            for(int i = 0; i < 6; i++) bytes[5 - i] = (byte)((address >> (i * 8)) & 0xFF);
            return string.Join(":", bytes.Select(b => b.ToString("X2")));
        }

        public static bool IsJabraName(string name) {
            if(string.IsNullOrWhiteSpace(name)) return false;
            string n = name.Trim();
            return n.IndexOf("Jabra", StringComparison.OrdinalIgnoreCase) >= 0
                || n.StartsWith("Evolve", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Engage", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Speak", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsJabraBluetoothAddress(string address) {
            string norm = NormalizeBluetoothAddress(address);
            if(norm.Length < 8) return false;
            string prefix = norm.Substring(0, 8);
            return KnownOuis.Any(o => string.Equals(o, prefix, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsJabraDevice(string name, string address) {
            return IsJabraName(name) || IsJabraBluetoothAddress(address);
        }

        public static byte GuessAudioKind(string name) {
            if(!string.IsNullOrWhiteSpace(name)) {
                if(name.IndexOf("Bud", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Elite", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Earbud", StringComparison.OrdinalIgnoreCase) >= 0) {
                    return 11; // Earbuds
                }
            }
            return 10; // Headset
        }

        public static string PairingInstructions(ReceiverKind kind, bool automatic) {
            switch(kind) {
                case ReceiverKind.LogitechUnifying:
                    return "Power the keyboard or mouse off, then on near the receiver.\nDevices pair automatically.";
                case ReceiverKind.JabraBluetoothDongle:
                    return "Put the Jabra headset/earbuds in Bluetooth pairing mode (hold the power/Bluetooth switch 3s until LED flashes blue, or hold both earbud buttons 3s).\n"
                        + (automatic ? "First nearby Jabra headset pairs automatically." : "Select the headset from the discovered list below.");
                case ReceiverKind.JabraDectDongle:
                    return "Plug the Jabra Engage DECT headset into this PC with its USB cable while the Link 400 dongle is attached.\nDongleDeck will pair them automatically over USB.";
                case ReceiverKind.WindowsBluetooth:
                    return "Put the Jabra headset/earbuds in Bluetooth pairing mode (flashing blue LED).\nSelect the discovered device below to pair directly with Windows Bluetooth.";
                default:
                    return "Blue receiver channel \u2192 power off \u2192 hold a key/button \u2192 power on.\n"
                        + (automatic ? "Devices pair automatically." : "Choose a device below.");
            }
        }
    }

    public sealed class JabraDirectStatus {
        public bool Running;
        public string Notice {
            get {
                return Running
                    ? "Jabra Direct is running in the background. If a Jabra Link dongle times out, close Jabra Direct and click Refresh."
                    : "";
            }
        }

        public static JabraDirectStatus Read() {
            try {
                bool running = Process.GetProcesses().Any(p => {
                    try {
                        string n = p.ProcessName;
                        return n.IndexOf("jabra-direct", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.Equals("JabraDirect", StringComparison.OrdinalIgnoreCase);
                    } catch { return false; }
                    finally { p.Dispose(); }
                });
                return new JabraDirectStatus { Running = running };
            } catch {
                return new JabraDirectStatus { Running = false };
            }
        }
    }

    internal sealed class TrackedJabraDongle {
        public string Key;
        public IDevice Device;
        public IBluetoothDongle BluetoothDongle;
        public IDectDongle DectDongle;
        public bool IsDect { get { return DectDongle != null; } }
    }

    public sealed class JabraManager : IDisposable {
        public static readonly JabraManager Default = new JabraManager();

        readonly SemaphoreSlim startGate = new SemaphoreSlim(1, 1);
        readonly ConcurrentDictionary<string, TrackedJabraDongle> dongles = new ConcurrentDictionary<string, TrackedJabraDongle>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, IDectHeadsetOverUsb> usbDectHeadsets = new ConcurrentDictionary<string, IDectHeadsetOverUsb>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, IDevice> allDevices = new ConcurrentDictionary<string, IDevice>(StringComparer.OrdinalIgnoreCase);
        readonly List<IDisposable> subscriptions = new List<IDisposable>();

        IManualApi sdk;
        BluetoothModule btModule;
        DectModule dectModule;
        bool started;
        bool disposed;

        public string StartupError { get; private set; }
        public event Action TopologyChanged;

        JabraManager() {}

        static string FindDeviceConnectorPath() {
            try {
                string baseDir = AppContext.BaseDirectory;
                if(!string.IsNullOrEmpty(baseDir)) {
                    string candidate = Path.Combine(baseDir, "Jabra.Utilities", "DeviceConnector", "win32", "jabra-device-connector.exe");
                    if(File.Exists(candidate)) return candidate;
                }
            } catch {}
            return null;
        }

        public async Task<bool> EnsureStartedAsync() {
            if(disposed) return false;
            if(started) return true;
            await startGate.WaitAsync().ConfigureAwait(false);
            try {
                if(started) return true;
                StartupError = null;
                var config = new Config(null, "DongleDeckPrime", "DongleDeck Prime");
                config.DeviceDiscovery.EnableBluetoothClassic = false;
                string connectorPath = FindDeviceConnectorPath();
                if(!string.IsNullOrEmpty(connectorPath)) {
                    config.DeviceDiscovery.DeviceConnectorPath = connectorPath;
                    AppLog.Raw("Jabra DeviceConnector path: " + connectorPath);
                }

                sdk = Init.InitManualSdk(config);
                subscriptions.Add(sdk.LogEvents.Subscribe(log => {
                    if(log.Level == LogLevel.Error) AppLog.Raw("Jabra SDK error: " + log.ToString());
                }));
                sdk.FatalError += (sender, args) => {
                    StartupError = "Jabra SDK stopped unexpectedly. Click Refresh or restart DongleDeck Prime.";
                    AppLog.Raw("Jabra SDK fatal error: " + (args != null ? args.ToString() : "unknown"));
                };

                btModule = new BluetoothModule(sdk);
                dectModule = new DectModule(sdk);

                var initialScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                subscriptions.Add(sdk.InitialScanComplete.Subscribe(_ => initialScan.TrySetResult(true)));
                subscriptions.Add(sdk.DeviceAdded.Subscribe(device => { _ = OnDeviceAddedAsync(device); }));
                subscriptions.Add(sdk.DeviceRemoved.Subscribe(OnDeviceRemoved));

                await sdk.Start().ConfigureAwait(false);
                await Task.WhenAny(initialScan.Task, Task.Delay(2500)).ConfigureAwait(false);
                // Allow any DeviceAdded async probes from initial scan to settle briefly.
                await Task.Delay(250).ConfigureAwait(false);
                started = true;
                return true;
            } catch(Exception ex) {
                StartupError = "Jabra SDK unavailable: " + ex.Message;
                AppLog.Raw("Jabra SDK start failed: " + ex);
                return false;
            } finally {
                startGate.Release();
            }
        }

        static string DeviceKey(IDevice device) {
            return "JABRA:" + (device.Id != null ? device.Id.ToString() : (device.ProductId.ToString("X4") + ":" + (device.SerialNumber ?? device.Name)));
        }

        async Task OnDeviceAddedAsync(IDevice device) {
            if(device == null || disposed) return;
            string key = DeviceKey(device);
            allDevices[key] = device;
            try {
                if(btModule != null) {
                    var btDongle = await btModule.TryCreateBluetoothDongle(device).ConfigureAwait(false);
                    if(btDongle != null) {
                        dongles[key] = new TrackedJabraDongle { Key = key, Device = device, BluetoothDongle = btDongle };
                        AppLog.Raw("Jabra BT dongle attached: " + device.Name + " (" + device.ProductId.ToString("X4") + ")");
                        RaiseTopologyChanged();
                        return;
                    }
                }
                if(dectModule != null) {
                    if(await DectModule.IsDectDongle(device).ConfigureAwait(false)) {
                        var dectDongle = await dectModule.ConvertToDectDongle(device).ConfigureAwait(false);
                        if(dectDongle != null) {
                            dongles[key] = new TrackedJabraDongle { Key = key, Device = device, DectDongle = dectDongle };
                            AppLog.Raw("Jabra DECT dongle attached: " + device.Name + " (" + device.ProductId.ToString("X4") + ")");
                            RaiseTopologyChanged();
                            return;
                        }
                    } else if(await DectModule.IsDectHeadsetOverUsb(device).ConfigureAwait(false)) {
                        var usbHeadset = await dectModule.ConvertToDectHeadsetOverUsb(device).ConfigureAwait(false);
                        if(usbHeadset != null) {
                            usbDectHeadsets[key] = usbHeadset;
                            AppLog.Raw("Jabra USB DECT headset attached: " + device.Name);
                            RaiseTopologyChanged();
                            return;
                        }
                    }
                }
            } catch(Exception ex) {
                AppLog.Raw("Jabra device probe failed for " + device.Name + ": " + ex.Message);
            }
        }

        void OnDeviceRemoved(IDevice device) {
            if(device == null) return;
            string key = DeviceKey(device);
            IDevice removedDev;
            allDevices.TryRemove(key, out removedDev);
            TrackedJabraDongle removedDongle;
            IDectHeadsetOverUsb removedHeadset;
            bool changed = dongles.TryRemove(key, out removedDongle) | usbDectHeadsets.TryRemove(key, out removedHeadset);
            if(changed) {
                AppLog.Raw("Jabra device detached: " + device.Name);
                RaiseTopologyChanged();
            }
        }

        void RaiseTopologyChanged() {
            var handler = TopologyChanged;
            if(handler != null) handler();
        }

        public async Task<List<ReceiverSnapshot>> InspectAsync() {
            var snapshots = new List<ReceiverSnapshot>();
            if(!await EnsureStartedAsync().ConfigureAwait(false)) return snapshots;

            // Ensure any already-present devices in sdk.CurrentDevices have been probed.
            if(sdk != null) {
                foreach(var dev in sdk.CurrentDevices) {
                    string k = DeviceKey(dev);
                    if(!allDevices.ContainsKey(k)) {
                        await OnDeviceAddedAsync(dev).ConfigureAwait(false);
                    }
                }
            }

            int index = 0;
            foreach(var tracked in dongles.Values.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase)) {
                index++;
                var dev = tracked.Device;
                string serial = string.IsNullOrWhiteSpace(dev.SerialNumber) ? tracked.Key : ("JABRA\\" + dev.ProductId.ToString("X4") + "\\" + dev.SerialNumber.Trim().ToUpperInvariant());
                var info = new ReceiverInfo {
                    Kind = tracked.IsDect ? ReceiverKind.JabraDectDongle : ReceiverKind.JabraBluetoothDongle,
                    Key = tracked.Key,
                    PhysicalDeviceId = serial,
                    UsbId = dev.VendorId.ToString("X4") + ":" + dev.ProductId.ToString("X4"),
                    Revision = string.IsNullOrWhiteSpace(dev.SerialNumber) ? (tracked.IsDect ? "DECT" : "Bluetooth") : ("SN " + dev.SerialNumber),
                    Label = string.IsNullOrWhiteSpace(dev.Name) ? ("Jabra Dongle " + index) : dev.Name,
                    MaxSlots = tracked.IsDect ? 1 : 8
                };

                var slots = new List<DeviceSlot>();
                if(!tracked.IsDect && tracked.BluetoothDongle != null) {
                    try {
                        var list = await tracked.BluetoothDongle.GetPairingList().ConfigureAwait(false);
                        int slotNum = 0;
                        foreach(var entry in list) {
                            slotNum++;
                            string addr = JabraClassifier.NormalizeBluetoothAddress(entry.BluetoothAddress != null ? entry.BluetoothAddress.AsHexString() : "");
                            string name = string.IsNullOrWhiteSpace(entry.BluetoothName) ? ("Bluetooth Audio " + slotNum) : entry.BluetoothName.Trim();
                            bool connected = entry.ConnectionStatus == JabraBtStatus.CONNECTED;
                            string radioTag = entry.IsPairedViaLe && entry.IsPairedViaClassic ? "Classic + LE" : (entry.IsPairedViaLe ? "BT LE" : "Bluetooth");
                            slots.Add(new DeviceSlot {
                                Number = slotNum,
                                Kind = JabraClassifier.GuessAudioKind(name),
                                Id = string.IsNullOrEmpty(addr) ? ("ENTRY-" + slotNum) : addr,
                                TransportId = addr,
                                Model = name,
                                Brand = JabraClassifier.IsJabraDevice(name, addr) ? "Jabra" : "Bluetooth",
                                ReportedOnline = connected,
                                IsConnected = connected,
                                SupportsConnect = true,
                                ConnectionTag = radioTag,
                                LastSeenUtc = connected ? DateTime.UtcNow : DateTime.MinValue
                            });
                        }
                    } catch(Exception ex) {
                        AppLog.Raw("Jabra GetPairingList failed for " + info.Label + ": " + ex.Message);
                    }
                } else if(tracked.IsDect) {
                    // For Link 400 DECT dongles, show any indirect connected headset or USB DECT headset ready for pairing.
                    var pairedChild = allDevices.Values.FirstOrDefault(d => !object.Equals(d.Id, dev.Id) && d.Type == Jabra.NET.Sdk.Core.Types.DeviceType.Headset && !usbDectHeadsets.ContainsKey(DeviceKey(d)));
                    if(pairedChild != null) {
                        slots.Add(new DeviceSlot {
                            Number = 1,
                            Kind = 10,
                            Id = string.IsNullOrWhiteSpace(pairedChild.SerialNumber) ? pairedChild.ProductId.ToString("X4") : pairedChild.SerialNumber,
                            Model = pairedChild.Name,
                            Brand = "Jabra",
                            ReportedOnline = true,
                            IsConnected = true,
                            SupportsConnect = false,
                            ConnectionTag = "DECT",
                            LastSeenUtc = DateTime.UtcNow
                        });
                    }
                }

                snapshots.Add(new ReceiverSnapshot { Receiver = info, Slots = slots });
            }
            return snapshots;
        }

        TrackedJabraDongle RequireDongle(ReceiverInfo receiver) {
            TrackedJabraDongle tracked;
            if(!dongles.TryGetValue(receiver.Key, out tracked)) {
                throw new InvalidOperationException("The selected Jabra dongle was unplugged. Refresh the receiver list.");
            }
            return tracked;
        }

        public async Task<DeviceSlot> PairBluetoothAsync(ReceiverInfo receiver, PairingSession session, IProgress<PairProgress> progress, CancellationToken stop) {
            DeviceLockStore.Default.EnsureCanPair(receiver);
            var tracked = RequireDongle(receiver);
            if(tracked.BluetoothDongle == null) throw new InvalidOperationException("Selected Jabra dongle does not support Bluetooth scanning.");
            var dongle = tracked.BluetoothDongle;

            var existing = await dongle.GetPairingList().ConfigureAwait(false);
            var existingAddresses = new HashSet<string>(existing.Select(e => JabraClassifier.NormalizeBluetoothAddress(e.BluetoothAddress != null ? e.BluetoothAddress.AsHexString() : "")), StringComparer.OrdinalIgnoreCase);

            var discovered = new ConcurrentDictionary<string, IScanEntry>(StringComparer.OrdinalIgnoreCase);
            var scanError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

            progress.Report(new PairProgress { Message = "Scanning nearby Bluetooth headsets\u2026", SecondsLeft = 0 });
            IDisposable scanSub = null;
            try {
                scanSub = dongle.ScanForDevicesInPairingMode(PairingTimeout.WaitForever).Subscribe(
                    entry => {
                        string addr = JabraClassifier.NormalizeBluetoothAddress(entry.BluetoothAddress != null ? entry.BluetoothAddress.AsHexString() : "");
                        if(string.IsNullOrEmpty(addr)) return;
                        if(!discovered.TryAdd(addr, entry)) return;
                        string name = string.IsNullOrWhiteSpace(entry.BluetoothName) ? ("Unknown (" + addr + ")") : entry.BluetoothName.Trim();
                        bool isJabra = JabraClassifier.IsJabraDevice(name, addr);
                        bool already = existingAddresses.Contains(addr);
                        var candidate = new Candidate {
                            Kind = JabraClassifier.GuessAudioKind(name),
                            Id = addr,
                            TransportId = addr,
                            Model = name,
                            Brand = isJabra ? "Jabra" : "Bluetooth",
                            IsJabra = isJabra,
                            AlreadyPaired = already
                        };
                        progress.Report(new PairProgress { Candidate = candidate, Message = "Headset found", SecondsLeft = 0 });
                        if(session.Automatic && isJabra && !already) {
                            session.Select(candidate);
                        }
                    },
                    err => scanError.TrySetResult(err)
                );

                Candidate chosen = null;
                while(chosen == null) {
                    stop.ThrowIfCancellationRequested();
                    if(scanError.Task.IsCompleted) {
                        var err = await scanError.Task.ConfigureAwait(false);
                        if(err != null) throw new IOException("Jabra dongle scan failed: " + err.Message, err);
                    }
                    chosen = session.TakeSelection();
                    if(chosen == null && session.Automatic) {
                        var firstJabra = discovered.Values.FirstOrDefault(e => {
                            string a = JabraClassifier.NormalizeBluetoothAddress(e.BluetoothAddress != null ? e.BluetoothAddress.AsHexString() : "");
                            return !existingAddresses.Contains(a) && JabraClassifier.IsJabraDevice(e.BluetoothName, a);
                        });
                        if(firstJabra != null) {
                            string a = JabraClassifier.NormalizeBluetoothAddress(firstJabra.BluetoothAddress.AsHexString());
                            chosen = new Candidate {
                                Kind = JabraClassifier.GuessAudioKind(firstJabra.BluetoothName),
                                Id = a,
                                TransportId = a,
                                Model = string.IsNullOrWhiteSpace(firstJabra.BluetoothName) ? a : firstJabra.BluetoothName.Trim(),
                                Brand = "Jabra",
                                IsJabra = true
                            };
                        }
                    }
                    if(chosen == null) await Task.Delay(120, stop).ConfigureAwait(false);
                }

                IScanEntry targetEntry;
                if(!discovered.TryGetValue(chosen.Id, out targetEntry)) {
                    throw new InvalidOperationException("Selected headset is no longer in the scan list.");
                }

                progress.Report(new PairProgress { Message = "Pairing and connecting " + chosen.Model + "\u2026", Selecting = true, SecondsLeft = 0 });
                try { await dongle.StopDeviceScanning().ConfigureAwait(false); } catch {}
                if(scanSub != null) { scanSub.Dispose(); scanSub = null; }

                await dongle.PairAndConnectTo(targetEntry, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return new DeviceSlot {
                    Number = existing.Count + 1,
                    Kind = chosen.Kind,
                    Id = chosen.Id,
                    TransportId = chosen.Id,
                    Model = chosen.Model,
                    Brand = chosen.Brand ?? "Jabra",
                    ReportedOnline = true,
                    IsConnected = true,
                    SupportsConnect = true,
                    LastSeenUtc = DateTime.UtcNow
                };
            } finally {
                if(scanSub != null) scanSub.Dispose();
                try { await dongle.StopDeviceScanning().ConfigureAwait(false); } catch {}
            }
        }

        public async Task<DeviceSlot> PairDectAsync(ReceiverInfo receiver, IProgress<PairProgress> progress, CancellationToken stop) {
            DeviceLockStore.Default.EnsureCanPair(receiver);
            var tracked = RequireDongle(receiver);
            if(tracked.DectDongle == null) throw new InvalidOperationException("Selected Jabra dongle is not a DECT adapter.");

            progress.Report(new PairProgress { Message = "Waiting for Jabra DECT headset via USB cable\u2026", SecondsLeft = 0 });
            IDectHeadsetOverUsb usbHeadset = null;
            while(usbHeadset == null) {
                stop.ThrowIfCancellationRequested();
                usbHeadset = usbDectHeadsets.Values.FirstOrDefault();
                if(usbHeadset == null) await Task.Delay(250, stop).ConfigureAwait(false);
            }

            progress.Report(new PairProgress { Message = "Pairing DECT headset with " + receiver.Label + "\u2026", Selecting = true, SecondsLeft = 0 });
            var paired = await tracked.DectDongle.PairWith(usbHeadset, stop).ConfigureAwait(false);
            return new DeviceSlot {
                Number = 1,
                Kind = 10,
                Id = string.IsNullOrWhiteSpace(paired.SerialNumber) ? paired.ProductId.ToString("X4") : paired.SerialNumber,
                Model = string.IsNullOrWhiteSpace(paired.Name) ? "Jabra DECT Headset" : paired.Name,
                Brand = "Jabra",
                ReportedOnline = true,
                IsConnected = true,
                ConnectionTag = "DECT",
                LastSeenUtc = DateTime.UtcNow
            };
        }

        public async Task ToggleConnectAsync(ReceiverInfo receiver, DeviceSlot slot) {
            var tracked = RequireDongle(receiver);
            if(tracked.BluetoothDongle == null) throw new InvalidOperationException("Connect/disconnect is only supported on Jabra Bluetooth dongles.");
            var dongle = tracked.BluetoothDongle;
            var list = await dongle.GetPairingList().ConfigureAwait(false);
            var entry = FindPairingEntry(list, slot);
            if(entry == null) throw new InvalidOperationException("Device is no longer paired to this Jabra dongle. Refresh to inspect.");

            if(entry.ConnectionStatus == JabraBtStatus.CONNECTED) {
                await dongle.DisconnectFrom(entry).ConfigureAwait(false);
            } else {
                await dongle.ConnectTo(entry, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
        }

        public async Task RemoveAsync(ReceiverInfo receiver, DeviceSlot slot) {
            DeviceLockStore.Default.EnsureCanRemove(receiver, slot);
            var tracked = RequireDongle(receiver);
            if(tracked.BluetoothDongle == null) throw new InvalidOperationException("Unpairing individual headsets is not supported on DECT Link 400 dongles; pair a new headset over USB to replace it.");
            var dongle = tracked.BluetoothDongle;
            var list = await dongle.GetPairingList().ConfigureAwait(false);
            var entry = FindPairingEntry(list, slot);
            if(entry == null) throw new InvalidOperationException("Device is no longer paired to this Jabra dongle. Refresh to inspect.");

            if(entry.ConnectionStatus == JabraBtStatus.CONNECTED || entry.ConnectionStatus == JabraBtStatus.CONNECTING) {
                try { await dongle.DisconnectFrom(entry).ConfigureAwait(false); } catch(Exception ex) { AppLog.Raw("Jabra pre-unpair disconnect: " + ex.Message); }
            }
            await dongle.Unpair(entry).ConfigureAwait(false);
        }

        public async Task ClearAsync(ReceiverInfo receiver, List<DeviceSlot> slots) {
            var targets = DeviceLockStore.Default.FilterClearable(receiver, slots);
            if(targets.Count == 0) return;
            var tracked = RequireDongle(receiver);
            if(tracked.BluetoothDongle == null) throw new InvalidOperationException("Clearing pairings is not supported on DECT Link 400 dongles.");
            var dongle = tracked.BluetoothDongle;
            var list = await dongle.GetPairingList().ConfigureAwait(false);
            foreach(var target in targets) {
                var entry = FindPairingEntry(list, target);
                if(entry == null) continue;
                if(entry.ConnectionStatus == JabraBtStatus.CONNECTED || entry.ConnectionStatus == JabraBtStatus.CONNECTING) {
                    try { await dongle.DisconnectFrom(entry).ConfigureAwait(false); } catch(Exception ex) { AppLog.Raw("Jabra pre-clear disconnect: " + ex.Message); }
                }
                await dongle.Unpair(entry).ConfigureAwait(false);
            }
        }

        static IPairingListEntry FindPairingEntry(IReadOnlyList<IPairingListEntry> list, DeviceSlot slot) {
            string targetAddr = JabraClassifier.NormalizeBluetoothAddress(slot.TransportId ?? slot.Id);
            return list.FirstOrDefault(e => {
                string addr = JabraClassifier.NormalizeBluetoothAddress(e.BluetoothAddress != null ? e.BluetoothAddress.AsHexString() : "");
                if(!string.IsNullOrEmpty(targetAddr) && string.Equals(addr, targetAddr, StringComparison.OrdinalIgnoreCase)) return true;
                return string.Equals(e.BluetoothName, slot.Model, StringComparison.OrdinalIgnoreCase);
            });
        }

        public void Dispose() {
            if(disposed) return;
            disposed = true;
            foreach(var sub in subscriptions) {
                try { sub.Dispose(); } catch {}
            }
            subscriptions.Clear();
            var disposableSdk = sdk as IDisposable;
            if(disposableSdk != null) {
                try { disposableSdk.Dispose(); } catch {}
            }
        }
    }
}
