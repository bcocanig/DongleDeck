# 30 ideas for the technician's bench

These are proposals, not features promised in v0.4.0. Numbers identify ideas rather than priority.

1. **Auto-stop after a combo:** stop when one new keyboard and one new mouse have been verified.
2. **Optional scan timer:** choose 30 seconds, 2 minutes, or continuous scanning.
3. **Receiver hotplug detection:** update the receiver list as dongles are inserted or removed.
4. **Remember the selected receiver:** keep the same physical dongle selected after refresh.
5. **USB port location:** show the hub and port path so identical dongles can be distinguished.
6. **Receiver aliases:** name dongles “Bench left,” “Loaner 12,” or an asset number.
7. **Device aliases:** add a useful name beside a model that reports an unexpected internal name.
8. **Barcode input:** scan receiver or combo asset labels directly into a job.
9. **Bench queue:** work through a list of combo sets with a clear next-job action.
10. **Controlled multi-receiver mode:** handle several dongles with an explicit target for each job.
11. **Combo checklist:** show keyboard paired, mouse paired, input verified, and ready to issue.
12. **Guided reconnect test:** power-cycle each device and record whether it reconnects.
13. **Input test pad:** visibly confirm keyboard and mouse activity without retaining typed text.
14. **Connection evidence:** distinguish a fresh receiver check-in from ordinary input activity.
15. **Adjustable activity window:** choose how quickly Active changes to Seen.
16. **Low-battery flags:** highlight devices that need fresh batteries before deployment.
17. **Battery freshness:** show when charge was actually reported rather than implying it is live.
18. **Optional sound cues:** different tones for a successful pair, a full receiver, and a failure.
19. **Keyboard shortcuts:** start/stop scanning and refresh without reaching for the mouse.
20. **Tray controls:** quick start/stop with a persistent scanning indicator.
21. **Large bench view:** larger controls and status text for a screen across the workbench.
22. **High-contrast/accessibility mode:** stronger focus outlines, readable scaling, and reduced animation.
23. **Session results:** show which devices were added, removed, or verified during the job.
24. **CSV inventory export:** export model, receiver asset label, battery, and verification result.
25. **Printable combo labels:** produce a label for the completed receiver, keyboard, and mouse set.
26. **Redacted diagnostics export:** package useful troubleshooting information with device IDs hidden by default.
27. **Compatibility catalogue:** show tested receiver revisions and pairing instructions for each device family.
28. **CLI automation:** list, pair, unpair, and clear for scripted bench workflows.
29. **Deployment package:** optional MSI/enterprise deployment and a signed executable.
30. **Logitech Unifying support:** a separate receiver backend with the same pairing and inventory UI.

## Next hardware phase: Logitech Unifying

Start with read-only identification of the attached receiver and its stored devices. Add pairing, removal, and completion verification against physical hardware after the protocol is understood. Reuse the bench UI, but implement Logitech's own receiver protocol separately from Dell's commands. Unifying, Bolt, Lightspeed, and Dell receivers are not interchangeable protocol targets.

No Logitech pairing commands are enabled in v0.4.0.
