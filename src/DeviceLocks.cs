using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DongleDeck {
    public sealed class DeviceLockedException : InvalidOperationException {
        public DeviceLockedException(string message) : base(message) {}
    }

    public sealed class DeviceLockEntry {
        public string HardwareKey { get; set; } = "";
        public string OwnerLabel { get; set; } = "";
        public string Model { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string ReceiverId { get; set; } = "";
        public DateTime LockedAtUtc { get; set; }
    }

    public sealed class ReceiverLockEntry {
        public string ReceiverId { get; set; } = "";
        public string OwnerLabel { get; set; } = "";
        public string Label { get; set; } = "";
        public DateTime LockedAtUtc { get; set; }
    }

    public sealed class LockStoreData {
        public int Version { get; set; } = 1;
        public string DefaultOwnerLabel { get; set; } = "";
        public List<DeviceLockEntry> Devices { get; set; } = new List<DeviceLockEntry>();
        public List<ReceiverLockEntry> Receivers { get; set; } = new List<ReceiverLockEntry>();
    }

    public sealed class DeviceLockStore {
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        readonly object gate = new object();
        readonly string storagePath;
        LockStoreData data = new LockStoreData();

        public static readonly DeviceLockStore Default = new DeviceLockStore(ResolveDefaultPath());

        public DeviceLockStore(string path) {
            storagePath = path;
            Load();
        }

        public static string ResolveDefaultPath() {
            try {
                string baseDir = AppContext.BaseDirectory;
                if(!string.IsNullOrEmpty(baseDir)) {
                    string portable = Path.Combine(baseDir, "locks.json");
                    if(File.Exists(portable)) return portable;
                }
            } catch {}
            return Path.Combine(AppLog.DirectoryPath, "locks.json");
        }

        public static string NormalizeOwner(string ownerLabel) {
            if(!string.IsNullOrWhiteSpace(ownerLabel)) return ownerLabel.Trim();
            string user = Environment.UserName;
            if(!string.IsNullOrWhiteSpace(user)) return user.Trim() + "'s Workstation";
            return "My Workstation";
        }

        public string DefaultOwnerLabel {
            get {
                lock(gate) {
                    return NormalizeOwner(data.DefaultOwnerLabel);
                }
            }
            set {
                lock(gate) {
                    data.DefaultOwnerLabel = NormalizeOwner(value);
                    SaveLocked();
                }
            }
        }

        public static string HardwareKey(DeviceSlot slot) {
            if(slot == null) return "";
            string brand = string.IsNullOrWhiteSpace(slot.Brand) ? "Unknown" : slot.Brand.Trim();
            string id = string.IsNullOrWhiteSpace(slot.Id) ? slot.Model ?? "" : slot.Id.Trim();
            return (brand + ":" + slot.Kind + ":" + id).ToUpperInvariant();
        }

        public static string ReceiverKey(ReceiverInfo receiver) {
            if(receiver == null) return "";
            return (receiver.StableId ?? "").Trim().ToUpperInvariant();
        }

        public void Load() {
            lock(gate) {
                try {
                    if(!File.Exists(storagePath)) {
                        data = new LockStoreData();
                        return;
                    }
                    string json = File.ReadAllText(storagePath);
                    var loaded = JsonSerializer.Deserialize<LockStoreData>(json, JsonOptions);
                    data = loaded ?? new LockStoreData();
                    if(data.Devices == null) data.Devices = new List<DeviceLockEntry>();
                    if(data.Receivers == null) data.Receivers = new List<ReceiverLockEntry>();
                } catch(Exception ex) {
                    AppLog.Raw("Lock store load failed: " + ex.Message);
                    data = new LockStoreData();
                }
            }
        }

        void SaveLocked() {
            try {
                string dir = Path.GetDirectoryName(storagePath);
                if(!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(data, JsonOptions);
                string temp = storagePath + ".tmp";
                File.WriteAllText(temp, json);
                if(File.Exists(storagePath)) File.Delete(storagePath);
                File.Move(temp, storagePath);
            } catch(Exception ex) {
                AppLog.Raw("Lock store save failed: " + ex.Message);
            }
        }

        public DeviceLockEntry GetDeviceLock(DeviceSlot slot) {
            string key = HardwareKey(slot);
            if(string.IsNullOrEmpty(key)) return null;
            lock(gate) {
                return data.Devices.FirstOrDefault(d => string.Equals(d.HardwareKey, key, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsDeviceLocked(DeviceSlot slot) {
            return GetDeviceLock(slot) != null;
        }

        public ReceiverLockEntry GetReceiverLock(ReceiverInfo receiver) {
            string key = ReceiverKey(receiver);
            if(string.IsNullOrEmpty(key)) return null;
            lock(gate) {
                return data.Receivers.FirstOrDefault(r => string.Equals(r.ReceiverId, key, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool IsReceiverLocked(ReceiverInfo receiver) {
            return GetReceiverLock(receiver) != null;
        }

        public DeviceLockEntry LockDevice(DeviceSlot slot, ReceiverInfo receiver, string ownerLabel) {
            if(slot == null) throw new ArgumentNullException("slot");
            string key = HardwareKey(slot);
            string owner = NormalizeOwner(ownerLabel);
            lock(gate) {
                data.DefaultOwnerLabel = owner;
                var existing = data.Devices.FirstOrDefault(d => string.Equals(d.HardwareKey, key, StringComparison.OrdinalIgnoreCase));
                if(existing == null) {
                    existing = new DeviceLockEntry { HardwareKey = key };
                    data.Devices.Add(existing);
                }
                existing.OwnerLabel = owner;
                existing.Model = slot.Model ?? "";
                existing.DeviceType = slot.DeviceType;
                existing.ReceiverId = ReceiverKey(receiver);
                existing.LockedAtUtc = DateTime.UtcNow;
                SaveLocked();
                return existing;
            }
        }

        public bool UnlockDevice(DeviceSlot slot) {
            string key = HardwareKey(slot);
            if(string.IsNullOrEmpty(key)) return false;
            lock(gate) {
                int removed = data.Devices.RemoveAll(d => string.Equals(d.HardwareKey, key, StringComparison.OrdinalIgnoreCase));
                if(removed > 0) SaveLocked();
                return removed > 0;
            }
        }

        public ReceiverLockEntry LockReceiver(ReceiverInfo receiver, string ownerLabel) {
            if(receiver == null) throw new ArgumentNullException("receiver");
            string key = ReceiverKey(receiver);
            if(string.IsNullOrEmpty(key)) throw new InvalidOperationException("This receiver does not have a stable hardware identifier.");
            string owner = NormalizeOwner(ownerLabel);
            lock(gate) {
                data.DefaultOwnerLabel = owner;
                var existing = data.Receivers.FirstOrDefault(r => string.Equals(r.ReceiverId, key, StringComparison.OrdinalIgnoreCase));
                if(existing == null) {
                    existing = new ReceiverLockEntry { ReceiverId = key };
                    data.Receivers.Add(existing);
                }
                existing.OwnerLabel = owner;
                existing.Label = receiver.Label ?? "";
                existing.LockedAtUtc = DateTime.UtcNow;
                SaveLocked();
                return existing;
            }
        }

        public bool UnlockReceiver(ReceiverInfo receiver) {
            string key = ReceiverKey(receiver);
            if(string.IsNullOrEmpty(key)) return false;
            lock(gate) {
                int removed = data.Receivers.RemoveAll(r => string.Equals(r.ReceiverId, key, StringComparison.OrdinalIgnoreCase));
                if(removed > 0) SaveLocked();
                return removed > 0;
            }
        }

        public int LockCombo(ReceiverInfo receiver, IEnumerable<DeviceSlot> slots, string ownerLabel, bool lockReceiver) {
            string owner = NormalizeOwner(ownerLabel);
            int count = 0;
            lock(gate) {
                data.DefaultOwnerLabel = owner;
                if(lockReceiver && receiver != null && !string.IsNullOrEmpty(ReceiverKey(receiver))) {
                    string rkey = ReceiverKey(receiver);
                    var existing = data.Receivers.FirstOrDefault(r => string.Equals(r.ReceiverId, rkey, StringComparison.OrdinalIgnoreCase));
                    if(existing == null) {
                        existing = new ReceiverLockEntry { ReceiverId = rkey };
                        data.Receivers.Add(existing);
                    }
                    existing.OwnerLabel = owner;
                    existing.Label = receiver.Label ?? "";
                    existing.LockedAtUtc = DateTime.UtcNow;
                }
                if(slots != null) {
                    foreach(var slot in slots) {
                        if(slot == null) continue;
                        string key = HardwareKey(slot);
                        if(string.IsNullOrEmpty(key)) continue;
                        var existing = data.Devices.FirstOrDefault(d => string.Equals(d.HardwareKey, key, StringComparison.OrdinalIgnoreCase));
                        if(existing == null) {
                            existing = new DeviceLockEntry { HardwareKey = key };
                            data.Devices.Add(existing);
                        }
                        existing.OwnerLabel = owner;
                        existing.Model = slot.Model ?? "";
                        existing.DeviceType = slot.DeviceType;
                        existing.ReceiverId = ReceiverKey(receiver);
                        existing.LockedAtUtc = DateTime.UtcNow;
                        count++;
                    }
                }
                SaveLocked();
            }
            return count;
        }

        public void ApplyTo(ReceiverSnapshot snapshot) {
            if(snapshot == null || snapshot.Receiver == null) return;
            var rLock = GetReceiverLock(snapshot.Receiver);
            snapshot.Receiver.IsLocked = rLock != null;
            snapshot.Receiver.OwnerLabel = rLock != null ? rLock.OwnerLabel : null;
            if(snapshot.Slots == null) return;
            foreach(var slot in snapshot.Slots) {
                var dLock = GetDeviceLock(slot);
                slot.IsLocked = dLock != null;
                slot.OwnerLabel = dLock != null ? dLock.OwnerLabel : (rLock != null ? rLock.OwnerLabel : null);
                slot.ReceiverLocked = snapshot.Receiver.IsLocked;
                slot.NotifyLockState();
            }
        }

        public void EnsureCanRemove(ReceiverInfo receiver, DeviceSlot slot) {
            var rLock = GetReceiverLock(receiver);
            if(rLock != null) throw new DeviceLockedException("Receiver is locked (" + rLock.OwnerLabel + "). Unlock the receiver before removing devices.");
            var dLock = GetDeviceLock(slot);
            if(dLock != null) throw new DeviceLockedException((slot.Model ?? slot.DeviceType) + " is locked (" + dLock.OwnerLabel + "). Unlock it first to unpair.");
        }

        public void EnsureCanPair(ReceiverInfo receiver) {
            var rLock = GetReceiverLock(receiver);
            if(rLock != null) throw new DeviceLockedException("Receiver is locked (" + rLock.OwnerLabel + "). Unlock it before pairing new devices onto it.");
        }

        public List<DeviceSlot> FilterClearable(ReceiverInfo receiver, IEnumerable<DeviceSlot> slots) {
            var rLock = GetReceiverLock(receiver);
            if(rLock != null) throw new DeviceLockedException("Receiver is locked (" + rLock.OwnerLabel + "). Unlock the receiver before clearing pairings.");
            if(slots == null) return new List<DeviceSlot>();
            return slots.Where(s => !IsDeviceLocked(s)).ToList();
        }
    }
}
