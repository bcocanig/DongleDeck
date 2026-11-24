using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using WinBtStatus = Windows.Devices.Bluetooth.BluetoothConnectionStatus;

namespace DongleDeck {
    public static class WindowsBluetoothOperations {
        public const string ReceiverKey = "WINDOWS_BLUETOOTH_RADIO";

        static readonly string[] AdditionalProperties = new[] {
            "System.Devices.Aep.DeviceAddress",
            "System.Devices.Aep.IsConnected",
            "System.Devices.Aep.IsPaired",
            "System.Devices.Aep.IsPresent",
            "System.Devices.Aep.Category"
        };

        public static async Task<ReceiverSnapshot> InspectAsync(bool jabraOnly) {
            BluetoothAdapter adapter = null;
            try {
                adapter = await BluetoothAdapter.GetDefaultAsync();
            } catch(Exception ex) {
                AppLog.Raw("Windows Bluetooth adapter check failed: " + ex.Message);
                return null;
            }
            if(adapter == null) return null;

            string adapterAddr = JabraClassifier.FormatBluetoothAddress(adapter.BluetoothAddress);
            var info = new ReceiverInfo {
                Kind = ReceiverKind.WindowsBluetooth,
                Key = ReceiverKey,
                PhysicalDeviceId = string.IsNullOrEmpty(adapterAddr) ? ReceiverKey : ("BTH\\ADAPTER\\" + adapterAddr),
                UsbId = string.IsNullOrEmpty(adapterAddr) ? "PC Radio" : adapterAddr,
                Revision = adapter.IsLowEnergySupported ? "Classic + LE" : "Classic",
                Label = "Windows Bluetooth (PC Radio)",
                MaxSlots = null
            };

            var slots = new List<DeviceSlot>();
            try {
                string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var paired = await DeviceInformation.FindAllAsync(selector, AdditionalProperties);
                int slotNum = 0;
                foreach(var devInfo in paired) {
                    string name = string.IsNullOrWhiteSpace(devInfo.Name) ? "Bluetooth Device" : devInfo.Name.Trim();
                    string addr = ExtractAddress(devInfo);
                    bool isJabra = JabraClassifier.IsJabraDevice(name, addr);
                    bool isAudio = IsAudioDeviceInfo(devInfo, name);
                    bool isConnected = ReadBoolProperty(devInfo, "System.Devices.Aep.IsConnected");

                    try {
                        using(var bt = await BluetoothDevice.FromIdAsync(devInfo.Id)) {
                            if(bt != null) {
                                if(string.IsNullOrEmpty(addr)) addr = JabraClassifier.FormatBluetoothAddress(bt.BluetoothAddress);
                                isJabra = isJabra || JabraClassifier.IsJabraDevice(bt.Name, addr);
                                if(bt.ClassOfDevice != null && bt.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo) isAudio = true;
                                isConnected = bt.ConnectionStatus == WinBtStatus.Connected;
                            }
                        }
                    } catch(Exception ex) {
                        AppLog.Raw("Windows BT inspect detail skipped for " + name + ": " + ex.Message);
                    }

                    if(jabraOnly && !isJabra) continue;
                    if(!jabraOnly && !isJabra && !isAudio) continue;

                    slotNum++;
                    slots.Add(new DeviceSlot {
                        Number = slotNum,
                        Kind = JabraClassifier.GuessAudioKind(name),
                        Id = string.IsNullOrEmpty(addr) ? devInfo.Id : addr,
                        TransportId = devInfo.Id,
                        Model = name,
                        Brand = isJabra ? "Jabra" : "Bluetooth",
                        ReportedOnline = isConnected,
                        IsConnected = isConnected,
                        SupportsConnect = true,
                        ConnectionTag = "Windows BT",
                        LastSeenUtc = isConnected ? DateTime.UtcNow : DateTime.MinValue
                    });
                }
            } catch(Exception ex) {
                AppLog.Raw("Windows Bluetooth enumeration failed: " + ex.Message);
            }

            return new ReceiverSnapshot { Receiver = info, Slots = slots };
        }

        public static async Task<DeviceSlot> ScanAndPairAsync(ReceiverInfo receiver, PairingSession session, bool jabraOnly, IProgress<PairProgress> progress, CancellationToken stop) {
            DeviceLockStore.Default.EnsureCanPair(receiver);
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
            var discovered = new ConcurrentDictionary<string, DeviceInformation>(StringComparer.OrdinalIgnoreCase);

            var watcher = DeviceInformation.CreateWatcher(selector, AdditionalProperties);
            Action<DeviceInformation> handleDiscovered = devInfo => {
                if(devInfo == null || string.IsNullOrWhiteSpace(devInfo.Name)) return;
                string name = devInfo.Name.Trim();
                string addr = ExtractAddress(devInfo);
                bool isJabra = JabraClassifier.IsJabraDevice(name, addr);
                bool isAudio = IsAudioDeviceInfo(devInfo, name);
                if(jabraOnly && !isJabra) return;
                if(!jabraOnly && !isJabra && !isAudio) return;

                string key = string.IsNullOrEmpty(addr) ? devInfo.Id : addr;
                if(!discovered.TryAdd(key, devInfo)) return;

                var candidate = new Candidate {
                    Kind = JabraClassifier.GuessAudioKind(name),
                    Id = key,
                    TransportId = devInfo.Id,
                    Model = name,
                    Brand = isJabra ? "Jabra" : "Bluetooth",
                    IsJabra = isJabra,
                    AlreadyPaired = devInfo.Pairing != null && devInfo.Pairing.IsPaired
                };
                progress.Report(new PairProgress { Candidate = candidate, Message = "Bluetooth headset found", SecondsLeft = 0 });
            };

            watcher.Added += (s, devInfo) => handleDiscovered(devInfo);
            watcher.Updated += (s, update) => {
                // Newly updated endpoints sometimes gain a resolved friendly name after initial discovery.
            };

            progress.Report(new PairProgress { Message = jabraOnly ? "Scanning for nearby Jabra Bluetooth headsets\u2026" : "Scanning for nearby Bluetooth audio headsets\u2026", SecondsLeft = 0 });
            watcher.Start();
            try {
                Candidate chosen = null;
                while(chosen == null) {
                    stop.ThrowIfCancellationRequested();
                    chosen = session.TakeSelection();
                    if(chosen == null) await Task.Delay(120, stop).ConfigureAwait(false);
                }

                DeviceInformation targetInfo;
                if(!discovered.TryGetValue(chosen.Id, out targetInfo)) {
                    targetInfo = await DeviceInformation.CreateFromIdAsync(chosen.TransportId);
                }
                if(targetInfo == null) throw new InvalidOperationException("Could not resolve the selected Bluetooth headset.");

                progress.Report(new PairProgress { Message = "Pairing " + chosen.Model + " via Windows Bluetooth\u2026", Selecting = true, SecondsLeft = 0 });
                var custom = targetInfo.Pairing.Custom;
                Windows.Foundation.TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler = (sender, args) => {
                    if(args.PairingKind == DevicePairingKinds.ConfirmOnly || args.PairingKind == DevicePairingKinds.ConfirmPinMatch) {
                        args.Accept();
                    } else if(args.PairingKind == DevicePairingKinds.ProvidePin) {
                        args.Accept("0000");
                    }
                };
                custom.PairingRequested += handler;
                DevicePairingResult result;
                try {
                    result = await custom.PairAsync(
                        DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin,
                        DevicePairingProtectionLevel.Default);
                } finally {
                    custom.PairingRequested -= handler;
                }

                if(result.Status != DevicePairingResultStatus.Paired && result.Status != DevicePairingResultStatus.AlreadyPaired) {
                    throw new InvalidOperationException("Windows Bluetooth pairing did not complete (" + result.Status + "). Make sure the headset is flashing blue in pairing mode.");
                }

                return new DeviceSlot {
                    Number = 1,
                    Kind = chosen.Kind,
                    Id = chosen.Id,
                    TransportId = targetInfo.Id,
                    Model = chosen.Model,
                    Brand = chosen.Brand ?? "Jabra",
                    ReportedOnline = true,
                    IsConnected = true,
                    SupportsConnect = true,
                    ConnectionTag = "Windows BT",
                    LastSeenUtc = DateTime.UtcNow
                };
            } finally {
                try {
                    if(watcher.Status == DeviceWatcherStatus.Started || watcher.Status == DeviceWatcherStatus.EnumerationCompleted) {
                        watcher.Stop();
                    }
                } catch {}
            }
        }

        public static async Task RemoveAsync(ReceiverInfo receiver, DeviceSlot slot) {
            DeviceLockStore.Default.EnsureCanRemove(receiver, slot);
            if(string.IsNullOrEmpty(slot.TransportId)) throw new InvalidOperationException("Missing Windows Bluetooth device reference. Refresh and try again.");
            var info = await DeviceInformation.CreateFromIdAsync(slot.TransportId);
            if(info == null || !info.Pairing.IsPaired) return;
            var result = await info.Pairing.UnpairAsync();
            if(result.Status != DeviceUnpairingResultStatus.Unpaired && result.Status != DeviceUnpairingResultStatus.AlreadyUnpaired) {
                throw new InvalidOperationException("Windows Bluetooth unpair failed (" + result.Status + ").");
            }
        }

        public static async Task ClearAsync(ReceiverInfo receiver, List<DeviceSlot> slots) {
            var targets = DeviceLockStore.Default.FilterClearable(receiver, slots);
            foreach(var slot in targets) {
                await RemoveAsync(receiver, slot).ConfigureAwait(false);
            }
        }

        public static async Task<string> TryConnectAsync(DeviceSlot slot) {
            if(string.IsNullOrEmpty(slot.TransportId)) throw new InvalidOperationException("Missing Windows Bluetooth device reference.");
            using(var bt = await BluetoothDevice.FromIdAsync(slot.TransportId)) {
                if(bt == null) throw new InvalidOperationException("Bluetooth headset is no longer available.");
                if(bt.ConnectionStatus == WinBtStatus.Connected) {
                    OpenBluetoothSettings();
                    return "Opened Windows Bluetooth settings to manage the active audio link for " + slot.Model + ".";
                }
                try {
                    await bt.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                } catch(Exception ex) {
                    AppLog.Raw("Windows BT RFCOMM probe: " + ex.Message);
                }
                if(bt.ConnectionStatus == WinBtStatus.Connected) {
                    return slot.Model + " connected.";
                }
                OpenBluetoothSettings();
                return "Triggered reconnect for " + slot.Model + " and opened Windows Bluetooth settings if confirmation is needed.";
            }
        }

        public static void OpenBluetoothSettings() {
            try {
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            } catch(Exception ex) {
                AppLog.Raw("Open Bluetooth settings failed: " + ex.Message);
            }
        }

        static string ExtractAddress(DeviceInformation info) {
            object val;
            if(info.Properties != null && info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out val) && val != null) {
                return JabraClassifier.NormalizeBluetoothAddress(val.ToString());
            }
            return "";
        }

        static bool ReadBoolProperty(DeviceInformation info, string key) {
            object val;
            if(info.Properties != null && info.Properties.TryGetValue(key, out val) && val is bool) {
                return (bool)val;
            }
            return false;
        }

        static bool IsAudioDeviceInfo(DeviceInformation info, string name) {
            if(info.Properties != null) {
                object catObj;
                if(info.Properties.TryGetValue("System.Devices.Aep.Category", out catObj) && catObj is string[]) {
                    var cats = (string[])catObj;
                    if(cats.Any(c => c != null && c.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                }
            }
            if(!string.IsNullOrWhiteSpace(name)) {
                string[] audioHints = new[] { "Headset", "Headphone", "Earbud", "Buds", "Audio", "Speaker", "AirPods", "Evolve", "Engage", "Elite", "Poly", "Plantronics", "Sennheiser", "EPOS", "Bose", "Sony WH", "Sony WF" };
                if(audioHints.Any(h => name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            }
            return false;
        }
    }
}
