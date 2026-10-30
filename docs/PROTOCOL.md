# Protocol and implementation

## Where the knowledge came from

Michael Knap's [dell-rf](https://github.com/michaelknap/dell-rf) project published reverse-engineered protocol notes based on Dell Peripheral Manager USB captures. Its [4503 documentation](https://github.com/michaelknap/dell-rf/blob/main/docs/PROTOCOL-4503.md) supplied the starting point for discovery and pairing. That documented hardware is 413C:4503; this application's physical testing was on 413C:301C revision 0244.

The investigation also inspected installed Dell software to understand service access and the existence of internal pairing entry points. The shipped app does not call those private entry points, load Dell libraries, or include decompiled code. Its transport and UI are original code built with Windows APIs.

The practical breakthroughs were finding the separate management HID collection, using the correct 32-byte report format and command sequence, and releasing Dell's exclusive management handle by pausing `DPMService`. Those facts let a small utility reach functionality that Dell's much larger application normally controls.

## Layers

```text
DongleDeck WPF interface
    ↓ commands and status
ReceiverCore: discover / accept / verify / remove
    ↓ 32-byte USB HID reports
Windows hid.dll + setupapi.dll
    ↓ USB
Dell receiver firmware
    ↓ proprietary wireless pairing exchange
Keyboard or mouse
```

The application does not implement the radio stack, derive wireless encryption keys, replace firmware, or generate keyboard input. The dongle and peripheral perform their own wireless handshake after the app asks the receiver to accept the discovered device.

## Tested HID collections

| Purpose | Collection | Usage page / usage | Reports |
| --- | --- | --- | --- |
| Management | MI_02, COL02 | FF01 / 1 | Feature report 08, 32 bytes |
| Notifications | MI_02, COL01 | FF00 / 0 | Input report 01, 32 bytes |

`NativeHid.cs` enumerates only the enabled Dell USB ID and checks the HID capabilities. `HidD_SetFeature` and `HidD_GetFeature` exchange management reports. A separate overlapped input handle reads notifications.

## Commands used

All messages below are padded to 32 bytes. Hex values describe the tested receiver, not a vendor-supported public API.

| Prefix | Meaning |
| --- | --- |
| `08 01` | Read receiver information |
| `08 02` | Read paired-device count |
| `08 03 <slot>` | Read a stored slot, numbered 1–6 |
| `08 0B` | Begin a discovery attempt |
| `08 08` | Poll discovery |
| `08 04 01 <id0> <id1> <id2>` | Select a discovered peripheral for pairing |
| `08 05` | Poll pairing completion |
| `08 06 <slot>` | Remove that slot |
| `08 10 <slot>` | Query battery |

A discovery notification carries the peripheral type, model, and three-byte identifier. Automatic mode selects a newly discovered device; manual mode waits for the user's choice. Once selection is sent, the app polls completion, rereads stored slots, and checks that the new device exists and earlier devices remain. An attempt lasts about 30 seconds; the UI renews discovery while continuous scanning remains enabled.

Removal rereads the target's identity before sending one removal command. It verifies the resulting slot list and allows time for the device count to settle. Clear recalculates the next occupied slot after each removal because slot positions can change. Mutating removal commands are not blindly retried after an uncertain response.

## Live activity

Controlled MS5320W power cycles on the tested hardware produced:

- `01 06 <slot> 00`: disconnected.
- `01 06 <slot> 01`: reconnected.
- `01 08 <slot> <percentage>`: battery report.

Unknown notifications are ignored. Cached battery data alone does not establish presence.

`ActivityMonitor.cs` also registers Windows Raw Input to observe the routing header. It maps the physical USB parent to the selected receiver and uses the input type to identify a slot only when the mapping is unambiguous. It does not fetch the key or movement payload. Several saved mice of the same type can require a power cycle for exact identification.

## Files

| File | Responsibility |
| --- | --- |
| `App.xaml`, `App.cs` | Window, continuous scanning, automatic/manual selection, operation progress |
| `Theme.cs` | Dynamic dark/light palette and saved appearance preference |
| `NativeHid.cs` | Windows device enumeration, report transport, physical USB mapping |
| `ReceiverCore.cs` | Dell command handling, pairing/readback, removal, service lease, device status |
| `ActivityMonitor.cs` | Shared notifications and input-routing activity |
| `ProtocolTests.cs` | Simulated receiver tests; no real hardware commands |

## Limits

These commands are reverse-engineered and are not a promise of compatibility with every Dell dongle. The app currently enables one USB ID. It does not expose firmware updates, wireless key export, Bluetooth pairing, Logitech commands, or general-purpose arbitrary HID writes. UI activity labels report what was observed, not a guaranteed reachability test.
