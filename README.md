# FreeUSBEject

A small, free, open-source Windows tray tool for safely ejecting USB drives and external hard drives.

Click the tray icon, pick a drive, and it tells you when it's safe to unplug. No installer, no account, no ads.

## Features

- Detects USB flash drives, card readers and external hard drives (internal disks are never listed)
- Shows each drive's letter, label and size
- **Safe eject** – dismounts the volume and asks Windows to eject the device, like "Safely Remove Hardware"
- **Force eject** – closes programs running from the drive and dismounts it even if files are open
- Ejected drives disappear from the menu and from File Explorer
- Stays in the tray until you choose **Exit**; the menu closes when you click elsewhere
- Optional **Start with Windows** (per-user, no admin rights needed)
- Single portable `.exe`

## Requirements

- Windows 10 or 11 (uses the .NET Framework 4 that ships with Windows)

## Build

No SDK required. On Windows, double-click `build.bat`, or run:

```bat
build.bat
```

The program is created at `bin\FreeUSBEject.exe`. It is a standalone single file (icon embedded).

## Usage

1. Run `FreeUSBEject.exe` – it appears in the system tray.
2. Left-click the icon to list your USB / external drives.
3. Choose **Eject X:** for a normal safe eject, or **Force eject X:** if a program is holding the drive.
4. Tick **Start with Windows** if you want it to launch at login. Choose **Exit** to close it.

Errors are written to `FreeUSBEject.log` next to the `.exe`.

## Force eject: know the risk

Force eject asks programs running from the drive to close (killing them after about 2 seconds), then dismounts
the volume even if other programs still have files open. If a program is **writing to the drive at that moment**,
its unsaved data can be lost. Use a normal eject whenever possible.

## How it works

- Drives are detected by bus type (`IOCTL_STORAGE_QUERY_PROPERTY`: USB / IEEE 1394).
- Eject: lock + dismount the volume (`FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`), release the handles, then
  `CM_Request_Device_Eject` (Plug and Play). Falls back to `IOCTL_STORAGE_EJECT_MEDIA`.
- After a successful eject the drive letter is released and Explorer is notified (`SHChangeNotify`).

## Status

The project is young and has had limited testing across hardware. Bug reports are welcome. If something fails,
please include the last lines of `FreeUSBEject.log`, your Windows version and the drive type.

## License

The source code is released under the [MIT License](LICENSE).

The icon in `assets/` is **not** covered by the MIT license. It was obtained separately; check its original license
and attribution requirements before redistributing, or replace it with your own artwork (the build works without it).

This project is independent and is not affiliated with or derived from any commercial USB-ejecting product.
