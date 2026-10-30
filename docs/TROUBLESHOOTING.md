# Troubleshooting

## No receiver appears

Check that the receiver has USB ID **413C:301C**, then click Refresh. Revision 0244 is the tested hardware. Another dongle with a similar blue icon can use a different protocol and is not automatically supported.

In Windows Device Manager, the USB ID appears under the device's **Properties → Details → Hardware Ids**. Include that ID and revision in a compatibility report, without serial numbers.

## Access denied or another tool cannot use the receiver

Close other pairing utilities and run DongleDeck with its administrator prompt approved. Dell's `DPMService` can hold the management interface exclusively. DongleDeck pauses a running service during management operations and restores it afterward; it does not install or require that service.

Continuous pairing uses the receiver's management interface during discovery attempts. Click Stop pairing before returning to Dell's pairing application.

## Nothing is discovered

Use the blue receiver channel on the keyboard or mouse. Switch it off, hold a key or button, and switch it back on. Bluetooth channels do not pair to the USB receiver. Replace weak batteries if needed.

Automatic pairing accepts a newly discovered compatible device. To choose manually, uncheck Automatic pairing before selecting a discovered device.

## Pairing takes several seconds

The receiver may take several seconds to save a device. The footer names the current step and shows elapsed seconds. Stop pairing waits for an already-selected device's verification before ending the session.

If a result is uncertain, click Refresh before making another change. A lost response does not necessarily mean the receiver ignored the operation.

## Activity or battery seems stale

Active means input or a live receiver check-in was observed within two seconds. Seen gives the age of the last observation. Inactive means no activity has been seen this session or the receiver reported a disconnect.

Power-cycle one device to identify its exact slot. Several stored devices of the same input type can make ordinary input ambiguous. Cached battery charge does not prove that a device is nearby.

## Runtime and script requirements

Use 64-bit Windows with .NET Framework 4.8 or newer. This is the Windows **.NET Framework** runtime, not the separate modern .NET SDK.

To build, use `build.ps1` in the source folder. If a downloaded script is blocked by the current execution policy, use the prebuilt release or launch a one-time process:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

This command does not change the machine's saved execution policy.

## Settings, logs, and uninstall

Settings and management-command logs live in `%LOCALAPPDATA%\DongleDeck` under the Windows account running the elevated application. If a different administrator account was entered at the prompt, look under that account.

Logs can contain peripheral and receiver identifiers. Review or redact them before sharing. Typed text and mouse movement/button payloads are not recorded by the input monitor.

To uninstall, close DongleDeck and delete the extracted folder. Delete the settings folder too if you want to remove its preferences and logs. No driver or background service is installed.
