# PVZOLBrowser

This repository has been replaced with the current `WinForms + NativeFlashProxy` implementation.

## Layout

- `BroswerWebBroswer/`
  Windows host application targeting `.NET Framework 4.8`.
- `NativeFlashProxy/`
  Native C++ proxy core, including local mapping and AMF support.
- `packages/log4net.1.2.10/`
  Minimal dependency kept for the legacy WinForms host.

## Build

### Native core

Build `NativeFlashProxy` first with CMake/MSVC so that:

- `NativeFlashProxy/build-win-x64/Release/flash_proxy_core.dll`

is produced.

### WinForms host

Then build:

- `BroswerWebBroswer/BroswerWebBroswer.csproj`

The host copies `flash_proxy_core.dll` from the native build output into its own output directory.

## Output

The Windows executable name is:

- `PVZOL浏览器.exe`
