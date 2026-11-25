# TempTray

Tray icons with live history graphs, in the style of Process Explorer's: CPU load, RAM use, and the temperatures and fan
speeds a Dell laptop's BIOS reports. One small exe, no installer, no driver, no dependencies.

Tested on a Dell XPS 15 9510 with Windows 11.

## Requirements

- Windows 10 or 11 with .NET Framework 4.8, which Windows includes.
- A Dell laptop whose BIOS exposes the Dell "data vault" WMI interface (`DDVWmiMethodFunction` in `root\wmi`). On other
  machines, CPU load and RAM still work and the menu says the Dell sensors are unavailable.
- Admin rights: the Dell interface answers only administrators, so the exe asks for them at start.

## Build

```
build.cmd
```

It compiles `TempTray.cs` into `TempTray.exe` with the C# compiler that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No SDK or packages needed.

## Run

```
TempTray.exe [--config <path>]
```

| Option | Meaning |
|---|---|
| `--config <path>` | Settings file to use. Default: `settings.txt` next to the exe. A relative path is resolved from the current folder. A missing file is created on the first change. |

Only one copy runs at a time.

New tray icons start in the overflow behind the `^` arrow. Drag them onto the taskbar, or switch TempTray on under
Settings → Personalization → Taskbar → Other system tray icons.

## Using it

Each item gets its own tray icon. Hover for the current value and the maximum over the last two minutes. Right-click any
icon for the menu:

| Menu item | What it does |
|---|---|
| Item checkboxes | Show or hide that item's icon. The menu stays open, so several can be toggled at once. The last visible icon can't be hidden, since the menu lives on the icons. |
| Draw a small number over the graph | Adds the value in a 3×5 pixel font: `87°`, `34%`, or `5.4` for a fan (thousands of rpm). |
| Show only the number, no graph | A big number on a colored background instead of the graph. |
| Start at logon (as admin, via Task Scheduler) | Creates or deletes the `TempTray` scheduled task that starts the app at logon with highest privileges, so there is no UAC prompt each time. It passes on `--config` if the app was started with it. |
| Exit | Closes the app. |

Graph colors:

| Item | Scale | Colors |
|---|---|---|
| CPU load | 0–100% | Green, with the kernel-mode share in red |
| RAM | 0–100% | Salmon |
| Temperatures | 30–100 °C | Green, orange from 65 °C, red from 80 °C |
| Fans | 0–6000 rpm | Green, orange, red at the same thresholds of the scale |

## Settings file

Plain text, rewritten on every menu change. It stores hidden rather than shown items, so an item the BIOS starts reporting
appears by itself.

```
mode=GraphWithNumber
hide:temp:08#1
hide:fan:02#1
```

| Line | Meaning |
|---|---|
| `mode=` | `Graph`, `GraphWithNumber` or `Number` |
| `hide:<key>` | One hidden item. Keys: `cpu-load`, `ram`, `battery`, `temp:<type>#<n>`, `fan:<type>#<n>` |

`<type>` is the BIOS sensor type in hex and `<n>` numbers sensors of the same type in BIOS order.

## Where the numbers come from

- **CPU load:** `GetSystemTimes`, as the busy share of time since the previous reading, every 2 seconds. Windows
  performance counters were avoided: their names are localized, so a query by name breaks on non-English Windows.
- **RAM:** `GlobalMemoryStatusEx`.
- **Temperatures and fans:** the Dell BIOS, through Windows' built-in ACPI WMI driver. All calls are read-only.
  - `ThermalSensorInformation` returns 5-byte entries (type, current °C, min, max, unknown), ended by `0xFF`.
  - `FanSensorInformation` returns 3-byte entries (type, rpm little-endian), ended by `0xFF`.
  - `BatteryTemperature` returns tenths of a kelvin.

  The layout and the type names follow the Linux
  [`dell-wmi-ddv`](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/dell/dell-wmi-ddv.c) driver.
  CPU (`0x00`), GPU (`0x11`) and the CPU and GPU fans are reliable; the others are best guesses or unnamed, so they show
  as "Sensor XX".

The Dell readings are what the BIOS reports for the whole CPU, not per core. Per-core temperatures need ring-0 access to
the CPU's thermal MSRs, which means a kernel driver; this app deliberately does without one.
