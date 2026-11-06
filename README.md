# OCKiller (WinUI 3)

A minimal WinUI 3 utility that changes your public IP through Cloudflare WARP.
It is a native .NET 10 port of the original `Reset-Usage-GUI.ps1` (PowerShell + WPF).

## What it does

1. Finds `warp-cli` (env `WARP_CLI` → `C:\Program Files\Cloudflare\Cloudflare WARP\warp-cli.exe` → `PATH`).
2. Reads the WARP mode (full tunnel / proxy) and managed (Zero Trust) state.
3. Shows the current IP from `https://www.cloudflare.com/cdn-cgi/trace`.
4. **Reset IP** runs `disconnect` → `registration delete` → `registration new` → `connect`
   and waits up to 30 seconds for the IP to change.
5. Copies the IP and offers an install link if WARP is missing.

The UI intentionally has no log panel. Activity is appended to a file instead:

```
%LOCALAPPDATA%\OCKiller\logs\ockiller.log
```

The file is rotated to `ockiller.log.old` at 5 MB. Use the **Open log** button
in the app to open it in Notepad.

## Requirements

- Windows 10 1809 or newer (tested on Windows 11)
- [Cloudflare WARP](https://1.1.1.1/) with `warp-cli`
- No .NET runtime or Windows App SDK install is needed — the published build is self-contained

## Run

Grab the latest `OCKiller-win-x64-v*.zip` from the
[Releases page](https://github.com/maewmod/OCKiller/releases/latest),
extract it, and run `OCKiller.exe` (or `OCKiller.bat`).

From a local build, double-click `dist\OCKiller.exe` or run `OCKiller.bat`.

Close opencode before resetting — the IP changes for the whole device.

## Build

```powershell
dotnet build .\OCKiller\OCKiller.csproj -c Debug
dotnet publish .\OCKiller\OCKiller.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o .\OCKiller\dist
```

## Notes

- The app is unpackaged (no MSIX) and runs directly from the `dist` folder.
- `dist` is large because it bundles the .NET runtime and Windows App SDK.
  For a smaller build, change `--self-contained true` to `false` (the target machine
  must then have the .NET 10 Desktop Runtime).
- If `warp-cli` lives somewhere else, set the `WARP_CLI` environment variable to its full path.
