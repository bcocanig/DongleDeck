# Contributing

DongleDeck currently targets one tested Dell receiver family. Useful contributions include clear bug reports, additional hardware results, UI improvements, and independently verified protocol work.

## Build and check

On Windows x64 with .NET Framework 4.8 or newer:

```powershell
.\test.ps1
.\build.ps1
.\pack.ps1
```

`test.ps1` uses a simulated transport. It does not access attached receivers. Build uses Windows' Framework compiler and WPF assemblies; no NuGet restore is needed. Portable release files go into `dist/`.

Keep changes focused. Protocol changes should include a meaningful simulated regression check and an explanation of any physical validation. UI changes should be checked in both light and dark themes. Do not introduce a new runtime dependency without explaining the installation impact.

## Hardware testing

Use a test receiver and peripherals you can re-pair. Report the USB vendor/product ID, receiver revision, peripheral model, Windows version, and whether pairing, removal, clear, battery, and reconnect behavior were actually tested.

Useful checks include pairing one keyboard and one mouse, preserving earlier pairings, stopping continuous scanning, removing one device, clearing the receiver, and confirming that a previously running Dell service is restored. Do not describe untested hardware as supported merely because an ID looks similar.

## Protocol boundaries

Dell and Logitech require separate receiver implementations. Do not send speculative pairing, erase, or firmware commands to unknown devices. Do not include Dell DLLs, decompiled source, credentials, wireless keys, device serial numbers, or unredacted hardware logs in commits or public issues.

Protocol research should cite its original source and distinguish captured behavior from assumptions. See [protocol notes](docs/PROTOCOL.md) and [acknowledgements](THIRD-PARTY-NOTICES.md).

## Feature proposals

The [30-item backlog](docs/ROADMAP.md) describes possibilities, not promised release dates. Explain the technician's task, current friction, and how the proposed change helps. Logitech Unifying is the next hardware investigation; it is not enabled in this release.
