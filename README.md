# ReaLynx — Atari Lynx Emulator for Windows

**ReaLynx** is a clean-room C# (.NET 8) Atari Lynx emulator in the REA emulator series.

[**Download Standalone EXE (v1.0.0)**](https://github.com/ScientificBrad/ReaLynx/releases/latest/download/ReaLynx.exe) · [**Portable ZIP (v1.0.0)**](https://github.com/ScientificBrad/ReaLynx/releases/latest/download/ReaLynx-v1.0.0-win-x64.zip) · [**All Releases**](https://github.com/ScientificBrad/ReaLynx/releases)

> Standalone Windows x64 builds require no .NET installation or administrator rights. Supply your own legally obtained 512-byte Atari Lynx boot ROM (`lynxboot.img`) and `.lnx`/`.lyx` cartridge images.

## Folder Layout
- `BOOTROM\lynxboot.img` — Legally supplied 512-byte original Atari Lynx boot ROM (not redistributed).
- `ROMs\` — User-supplied `.lnx` / `.lyx` cartridge images (not redistributed).
- `Launch ReaLynx.bat` — Double-click (or drop a `.lnx` file onto it) to launch the Release build.
- `ReaLynx\` — .NET 8 solution:
  - `ReaLynx.Core` — 65SC02 CPU, disassembler, `MAPCTL` ($FFF9) memory bus, Mikey ASIC (timers, interrupts, display DMA, 12-bit palette, 4-channel LFSR stereo audio, UART), Suzy ASIC (hardware math, SCB sprite engine, collision, controls), and `.lnx`/`.lyx` cartridge loader.
  - `ReaLynx.Win` — Windows frontend with drag-and-drop ROM loading, WinMM stereo audio, XInput + rebindable keyboard controls, fullscreen (`Alt+Enter`), fast-forward (`Tab`), integer/aspect scaling, auto + per-ROM rotation (`F8`), screenshots, recent files, and a live Hardware Debugger (`F9`).
  - `ReaLynx.Cli` — Headless deterministic runner for batch testing, scripted gameplay input, instruction tracing, and BMP screenshots.
  - `ReaLynx.Tests` — Automated xUnit test suite (`27 / 27` passing).

## Build & Launch
Requires **.NET 8 SDK** (or newer LTS) on Windows.

```powershell
# Build Release
dotnet build ReaLynx\ReaLynx.sln -c Release

# Run automated tests
dotnet test ReaLynx\ReaLynx.sln -c Release

# Launch Windows GUI
.\Launch ReaLynx.bat

# Headless CLI example
.\ReaLynx\ReaLynx.Cli\bin\Release\net8.0\ReaLynx.Cli.exe --boot .\BOOTROM\lynxboot.img --cart .\ROMs\game.lnx --frames 900 --screenshot out.bmp
```

## Controls & Shortcuts
Keyboard bindings are customizable via **Input → Configure Keyboard Controls...** (`%APPDATA%\ReaLynx\settings.json`). XInput controllers work automatically alongside keyboard input, and D-pad directions auto-rotate when the display is rotated.

| Lynx Control | Default Keyboard | XInput Gamepad |
|---|---|---|
| D-Pad | Arrow Keys | D-Pad / Left Stick |
| Button A (Outside) | `Z` | A |
| Button B (Inside) | `X` | B / X |
| Option 1 | `A` | LB / Y |
| Option 2 | `S` | RB / Back |
| Pause | `Enter` | Start |

- **Drag & Drop**: Drop any `.lnx`/`.lyx`/`.o` ROM (or 512-byte boot ROM) directly onto the window.
- `Alt+Enter` Fullscreen (`Esc` exits) · `Tab` (hold) 4x Fast-Forward · `Ctrl+O` Open Cartridge · `Ctrl+R` Reset · `F5` Pause/Resume · `F8` Cycle Rotation (saved per ROM) · `F9` Hardware Debugger · `F11` Single-Step · `F12` Screenshot · `Ctrl+M` Mute.

## Verified Compatibility
Tested on .NET 8 Release with real `lynxboot.img` (SHA-256 `c26a36c1...383`) across **all 88 primary ROMs** in `ROMs\`:
- **Full Library Sweep**: **88 / 88 (100%)** booted and rendered active video (86 standard `.lnx` cartridges through real RSA boot decryption + 2 BS93/BLL homebrew executables: *Lode Runner* and *T-Tris*).
- **Interactive Gameplay Verified** (title → button input → active gameplay + audio): *California Games* (3D tilting/scaling license plate + clouds), *Chip's Challenge*, *RoadBlasters* (3D highway driving + HUD), *Rygar* (Level 1 platforming + HUD), and *Lynx Casino*.

| Target / Cartridge | Boot | Graphics | Audio | Frames | Notes |
|---|---|---|---|---|---|
| **Boot ROM (`lynxboot.img`, no cart)** | PASS | PASS (`INSERT GAME`, 1,808 px) | Silent | 60 | Visually verified |
| **Blue Lightning (1989)** | PASS | PASS (4-quadrant logo) | Silent @ 850 | 850 | Visually verified |
| **California Games (1991)** | PASS | PASS (title screen) | Silent @ 750 | 750 | Visually verified |
| **Chip's Challenge (1989)** | PASS | PASS (title screen) | Silent @ 850 | 850 | Visually verified |
| **Batman Returns (1992)** | PASS | PASS (title screen) | PASS (active) | 900 | Visually verified |
| **Centipede (Prototype) (1987)** | PASS | PASS (vertical) | Silent @ 750 | 750 | Press `F8` once (header rot=0) |
| **Lynx Casino (1992)** | PASS | PASS (`LYNX` dice + lobby) | PASS (active) | 900 | Visually verified |
| **RoadBlasters (1990)** | PASS | PASS (3D road + HUD) | PASS (active) | 1000 | Visually verified |
| **Rampage (1991)** | PASS | PASS (title screen) | PASS (active) | 1000 | Visually verified |
| **Shadow of the Beast (1992)** | PASS | PASS (Psygnosis intro) | Silent @ 1000 | 1000 | Visually verified |
| **Klax (1990)** | PASS | PASS (vertical intro) | PASS (active) | 1000 | Press `F8` to rotate |
| **APB, Electrocop, Gauntlet, Paperboy, Rygar, Slime World, Toki, Warbirds, Zarlor Mercenary** | PASS | PASS (active framebuffers) | PASS (7/9 active @ 900) | 900 | Headless batch verified |

## Known Limitations
- **ComLynx Networking**: UART registers (`$FD8C`/`$FD8D`) support status and local loopback, without multi-instance network link emulation.
- **Cartridge Serial EEPROM**: 93C46/93C66/93C86 save EEPROMs are not yet implemented.
- **Suzy Bus Timing**: Sprite rendering completes synchronously on `SPRGO` rather than interleaving individual DMA cycles with CPU execution.
