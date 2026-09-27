# CleanMachine Privacy Policy

**Last updated: 27 September 2026**

## Summary

CleanMachine does not collect, transmit, sell or share any data. It contains
no advertising, no telemetry and no third-party analytics, and it makes no
network connections. Everything it stores stays on your own PC.

## What CleanMachine stores

CleanMachine keeps the following on your computer, in
`%LOCALAPPDATA%\CleanMachine`:

- **Settings** - your preferences, chosen exclusions, and enabled cleanup
  categories.
- **Cleanup statistics** - counts and sizes of what has been cleaned.
- **Activity history** - a log of each cleanup run, stored locally.
- **Registry backups** - `.reg` files CleanMachine writes before it removes any
  registry entry, so you can restore what it changed.

This data is written only to that folder. It is never uploaded, and nothing in
the app opens a network connection.

## What CleanMachine does not do

- It does not collect personal, financial, or usage information.
- It does not transmit anything to Microsoft or to any third party.
- It does not contain advertising, telemetry, or analytics of any kind.
- It does not read or modify files belonging to other applications except the
  temporary files and caches you explicitly select for cleaning.
- It does not request administrator rights. It runs entirely in your own user
  account and only touches per-user (HKCU) registry locations.

## Permissions

CleanMachine is packaged as a Windows app and declares the `runFullTrust`
capability, which Microsoft requires for desktop applications. Full trust is
needed because the app manages files and folders outside the app container -
temporary files, browser cache folders, and Recycle Bin contents - and reads
per-user registry keys for its Registry Care view.

Full trust does not grant additional data access on its own: it removes the
sandbox that would otherwise prevent the app from doing the file and registry
work you asked for. CleanMachine does not use it to download, install or
execute code, to access the network, or to elevate privileges.

## Uninstalling

Uninstalling from Windows Settings leaves your data folder in place so a
reinstall picks up where you left off. CleanMachine's own **Settings >
Uninstall CleanMachine** flow asks first and offers to delete
`%LOCALAPPDATA%\CleanMachine` as well. The default answer is to keep it.

## Children

CleanMachine is a system utility and is not directed at children.

## Changes to this policy

If this policy changes, the updated version will be published at the same URL
and the "Last updated" date above will change.

## Contact

Questions about this policy can be raised as an issue on the project's GitHub
repository.
