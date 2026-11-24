using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DongleDeck {
    internal static class PrimeTests {
        static void Check(bool value, string message) {
            if(!value) throw new Exception(message);
        }

        static void RejectLocked(Action action) {
            try {
                action();
            } catch(DeviceLockedException) {
                return;
            }
            throw new Exception("Expected DeviceLockedException");
        }

        internal static void Run(Action<string, Action> test) {
            test("Device lock persists across reload and follows device across slot numbers", delegate {
                string path = Path.Combine(Path.GetTempPath(), "dongledeck-lock-test-" + Guid.NewGuid().ToString("N") + ".json");
                try {
                    var store = new DeviceLockStore(path);
                    var receiver = new ReceiverInfo { Kind = ReceiverKind.DellUniversal, Key = "dell-1", PhysicalDeviceId = "USB\\VID_413C&PID_301C\\WORKSTATION", Label = "Dell Universal Receiver 1" };
                    var kb = new DeviceSlot { Number = 1, Kind = 1, Id = "010203", Model = "KB740", Brand = "Dell" };
                    var mouse = new DeviceSlot { Number = 2, Kind = 2, Id = "040506", Model = "MS5320W", Brand = "Dell" };
                    var guest = new DeviceSlot { Number = 3, Kind = 2, Id = "AABBCC", Model = "MS5320W", Brand = "Dell" };

                    int locked = store.LockCombo(receiver, new[] { kb, mouse }, "Paul's Workstation", lockReceiver: false);
                    Check(locked == 2, "Expected 2 locked combo devices");

                    // Reload from disk and verify locks follow devices even if slots shift.
                    var reloaded = new DeviceLockStore(path);
                    var kbMoved = new DeviceSlot { Number = 5, Kind = 1, Id = "010203", Model = "KB740", Brand = "Dell" };
                    var snap = new ReceiverSnapshot { Receiver = receiver, Slots = new List<DeviceSlot> { kbMoved, mouse, guest } };
                    reloaded.ApplyTo(snap);

                    Check(kbMoved.IsLocked && kbMoved.OwnerLabel == "Paul's Workstation" && !kbMoved.CanRemove, "Keyboard lock did not persist across slot shift");
                    Check(mouse.IsLocked && !mouse.CanRemove, "Mouse lock did not persist");
                    Check(!guest.IsLocked && guest.CanRemove, "Guest mouse should remain unlocked");

                    RejectLocked(() => reloaded.EnsureCanRemove(receiver, kbMoved));
                    var clearable = reloaded.FilterClearable(receiver, snap.Slots);
                    Check(clearable.Count == 1 && clearable[0].Id == "AABBCC", "Clear filter did not preserve locked combo");

                    // Unlocking allows removal again.
                    Check(reloaded.UnlockDevice(kbMoved), "UnlockDevice should return true");
                    reloaded.ApplyTo(snap);
                    Check(!kbMoved.IsLocked && kbMoved.CanRemove, "Unlocked keyboard should be removable");
                } finally {
                    if(File.Exists(path)) File.Delete(path);
                }
            });

            test("Receiver lock blocks pairing, removal, and clearing and labels receiver clearly", delegate {
                string path = Path.Combine(Path.GetTempPath(), "dongledeck-rxlock-test-" + Guid.NewGuid().ToString("N") + ".json");
                try {
                    var store = new DeviceLockStore(path);
                    var receiver = new ReceiverInfo { Kind = ReceiverKind.DellUniversal, Key = "dell-1", PhysicalDeviceId = "USB\\VID_413C&PID_301C\\MYDESK", Label = "Dell Universal Receiver 1" };
                    var slot = new DeviceSlot { Number = 1, Kind = 2, Id = "040506", Model = "MS5320W", Brand = "Dell" };
                    var snap = new ReceiverSnapshot { Receiver = receiver, Slots = new List<DeviceSlot> { slot } };

                    store.LockReceiver(receiver, "Paul's Desk");
                    store.ApplyTo(snap);

                    Check(receiver.IsLocked && receiver.ToString().Contains("[Paul's Desk]"), "Locked receiver label missing owner");
                    Check(!slot.CanRemove, "Slot on locked receiver must not be removable");
                    RejectLocked(() => store.EnsureCanPair(receiver));
                    RejectLocked(() => store.EnsureCanRemove(receiver, slot));
                    RejectLocked(() => store.FilterClearable(receiver, snap.Slots));

                    Check(store.UnlockReceiver(receiver), "UnlockReceiver should return true");
                    store.ApplyTo(snap);
                    Check(!receiver.IsLocked && slot.CanRemove, "Unlocked receiver should restore controls");
                } finally {
                    if(File.Exists(path)) File.Delete(path);
                }
            });

            test("Dell protocol ClearSelected removes only unlocked devices while preserving locked ones", delegate {
                var fake = FakeLink.Three();
                var all = ReceiverProtocol.ReadSlots(fake);
                var unlockedOnly = all.Where(s => s.Id == "070809").ToList();
                var remaining = ReceiverProtocol.ClearSelected(fake, all, unlockedOnly);
                Check(remaining.Count == 2 && remaining.Any(s => s.Id == "010203") && remaining.Any(s => s.Id == "040506"), "Locked devices were not preserved during selective clear");
            });

            test("Jabra classifier identifies headsets, earbuds, and GN Audio OUIs", delegate {
                Check(JabraClassifier.IsJabraDevice("Jabra Evolve2 65", "AA:BB:CC:11:22:33"), "Evolve2 not detected as Jabra");
                Check(JabraClassifier.IsJabraDevice("Engage 55", ""), "Engage not detected as Jabra");
                Check(JabraClassifier.IsJabraDevice("Unknown Headset", "70bf92aabbcc"), "GN Audio OUI not detected as Jabra");
                Check(!JabraClassifier.IsJabraDevice("Sony WH-1000XM5", "AA:BB:CC:DD:EE:FF"), "Non-Jabra headset falsely matched");
                Check(JabraClassifier.NormalizeBluetoothAddress("70bf92aabbcc") == "70:BF:92:AA:BB:CC", "BT address normalization failed");
                Check(JabraClassifier.FormatBluetoothAddress(0x70BF92AABBCCUL) == "70:BF:92:AA:BB:CC", "BT ulong address formatting failed");
                Check(JabraClassifier.GuessAudioKind("Jabra Elite 8 Active") == 11, "Elite should classify as Earbuds");
                Check(JabraClassifier.GuessAudioKind("Jabra Evolve2 Buds") == 11, "Evolve2 Buds should classify as Earbuds");
                Check(JabraClassifier.GuessAudioKind("Jabra Evolve2 75") == 10, "Evolve2 75 should classify as Headset");
            });

            test("Audio DeviceSlot renders headset title, connection status, and connect toggle", delegate {
                var headset = new DeviceSlot {
                    Number = 1,
                    Kind = 10,
                    Id = "70:BF:92:11:22:33",
                    Model = "Jabra Evolve2 65",
                    Brand = "Jabra",
                    SupportsConnect = true,
                    IsConnected = true,
                    ReportedOnline = true,
                    ConnectionTag = "Classic + LE"
                };
                Check(headset.Title == "Jabra Evolve2 65", "Audio title should display model name");
                Check(headset.ActivityAt(DateTime.UtcNow) == "Connected", "Connected audio activity text wrong");
                Check(headset.ConnectButtonText == "Disconnect", "Connect button text should show Disconnect");
                headset.IsConnected = false;
                headset.ReportedOnline = false;
                Check(headset.ActivityAt(DateTime.UtcNow) == "Paired \u00B7 Disconnected", "Disconnected audio activity text wrong");
                Check(headset.ConnectButtonText == "Connect", "Connect button text should show Connect");
            });
        }
    }
}
