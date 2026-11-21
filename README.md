```markdown
# XperiaSysMon

A lightweight, borderless fullscreen Windows system telemetry dashboard and touch-enabled macro deck built for smartphones repurposed as secondary desktop monitors (such as a Sony Xperia 5 III connected via Virtual Display Driver, Sunshine, and Artemis).

---

## Features

* **Real-Time Hardware Telemetry:** Monitors CPU load, per-thread utilization, and CPU package temperatures (via Core Temp shared memory), alongside NVIDIA GPU load, core clock speed, VRAM usage, and GPU temperature with thermal-reactive color shifting (`#38BDF8` / `#C084FC` → `#F59E0B` → `#EF4444`).
* **System & Storage Overview:** Displays live RAM usage, available memory, physical disk I/O activity, and a clean digital clock and date card.
* **Swipeable Touch Command Deck:** Swipe left anywhere on the touchscreen to switch smoothly from the telemetry view to a customizable 3×2 Stream Deck-style shortcut launcher.
* **JSON-Driven Macros & Custom Icons:** Configure tiles via `shortcuts.json` to launch applications, trigger single media/volume keys, execute multi-key hotkeys (such as `Win+Alt+R`), toggle cursor lock, or render custom `.png` icons alongside built-in `Segoe MDL2 Assets` vector glyphs.
* **Smart Multi-Monitor Focus & Cursor Locking:** Confines the Windows mouse cursor to your primary monitor (`Display 1`) while allowing direct touch interaction on your secondary phone display, and automatically redirects foreground focus back to your main monitor before firing screen-recording hotkeys so overlays capture the right screen.

---

## PC Setup (Virtual Display, Sunshine & XperiaSysMon)

1. **Set Up a Virtual Display:**
   * Install a Windows Virtual Display Driver (VDD) so Windows creates a secondary monitor (`Display 2`) matching your phone's aspect ratio (e.g., `2520x1080` or `1920x1080`) in **Windows Display Settings** (`Extend these displays`).
   * *(Tip)* Arrange `Display 2` diagonally at the top-right corner of `Display 1` to prevent accidental mouse drift.

2. **Install & Configure Sunshine:**
   * Download and install [Sunshine](https://github.com/LizardByte/Sunshine/releases) and open its web dashboard (`https://localhost:47990`).
   * Go to **Configuration** → **Audio/Video** and set **Output Name** to your secondary virtual display (`\\.\DISPLAY2`).
   * Under **Configuration** → **NVIDIA NVENC Encoder** (or your GPU's encoder tab), disable **AV1** and **HEVC** if using an older GPU (such as a GTX 1050) to enforce compatible **H.264** streaming.
   * Click **Save** and **Apply**.

3. **Run XperiaSysMon:**
   * Download `XperiaSysMon.exe` from the [Releases](../../releases) page, place it in a dedicated folder (e.g., `C:\Tools\`), and launch it.
   * The app automatically snaps borderless to `Display 2` and generates a default `shortcuts.json` file in the same directory.
   * *(Optional)* Keep **Core Temp** running in the background for live CPU temperature readouts.

---

## Android Client Setup (Artemis)

To use direct single-finger swiping and instant tile taps without trackpad cursor lag, pair your PC's Sunshine server with **Artemis** (ClassicOldSong's Android Moonlight fork):

1. **Install Artemis:** Download the latest `.apk` from the [official Artemis Releases page](https://github.com/ClassicOldSong/moonlight-android/releases) and install it on your Android device.
2. **Configure Touch & Video Settings:** Open **Artemis** → **Settings (gear icon)** and set:
   * **Touchscreen mode:** `Multi Touch` (maps finger taps and left/right swipes directly to exact pixel coordinates on `Display 2`).
   * **Video resolution & FPS:** Match your secondary virtual display resolution (e.g., `2520x1080` or `1080p`) at `30 FPS` or `60 FPS`.
   * **Video codec:** `H.264` (with **HDR** turned `Off`).
3. **Pair with Sunshine:** Connect your phone via USB Tethering (or local Wi-Fi), tap your PC in Artemis to generate a 4-digit PIN, and enter it into the **PIN** tab of Sunshine (`https://localhost:47990`).
4. **Launch:** Tap **Desktop** in Artemis to stream `XperiaSysMon.exe` fullscreen. Swipe left anywhere on the screen to open the **Command Deck**, and swipe right to return to the **System Monitor**.

---

## Customizing `shortcuts.json` & Custom Icons

Edit `shortcuts.json` in the same folder as `XperiaSysMon.exe` to customize your 3×2 Command Deck tiles:

```json
[
  {
    "Icon": "Icons/STEAM.png",
    "Title": "STEAM",
    "Subtitle": "BIG PICTURE",
    "Color": "#38BDF8",
    "ActionType": "App",
    "Target": "steam://open/bigpicture",
    "Arguments": ""
  },
  {
    "Icon": "CursorLock",
    "Title": "CURSOR LOCK",
    "Subtitle": "TOGGLE MONITOR",
    "Color": "#F59E0B",
    "ActionType": "CursorLock",
    "Target": "",
    "Arguments": ""
  },
  {
    "Icon": "Icons/RECORD.png",
    "Title": "RECORD",
    "Subtitle": "START/STOP",
    "Color": "#EF4444",
    "ActionType": "Hotkey",
    "Target": "Win+Alt+R",
    "Arguments": ""
  }
]

```

### Supported `ActionType` Values

| `ActionType` | Behavior | Example `Target` |
| --- | --- | --- |
| `App` | Launches an executable, URI protocol, or command | `"taskmgr.exe"`, `"steam://open/bigpicture"`, `"shutdown.exe"` (with `"Arguments": "/s /f /t 0"`) |
| `Hotkey` | Focuses the primary monitor and sends a multi-key chord | `"Win+Alt+R"`, `"Alt+F9"`, `"Ctrl+Shift+Esc"` |
| `Record` | Focuses the primary monitor and toggles recording (`Win+Alt+R` default) | `"Win+Alt+R"` |
| `Key` | Sends a single media or volume key | `"PlayPause"`, `"NextTrack"`, `"PrevTrack"`, `"Mute"`, `"VolUp"`, `"VolDown"` |
| `Screenshot` | Captures a full screenshot (`Win+PrintScreen`) | `""` |
| `CursorLock` | Toggles primary monitor mouse cursor confinement on/off | `""` |

### Custom PNG Icons

* Create an `Icons` folder next to `XperiaSysMon.exe` (e.g., `C:\Tools\Icons\`) and place transparent `.png` files inside.
* Set `"Icon": "Icons/your_icon.png"` in `shortcuts.json`. If the file path is not found, the tile automatically falls back to built-in vector glyph keywords (`Gamepad`, `PlayPause`, `NextTrack`, `Mute`, `CursorLock`, `TaskMgr`, `Record`, `Power`, `Spotify`, `Discord`, `Chrome`).

---

## Controls & Building from Source

* **Navigation:** Swipe left/right on the touchscreen or use the `Left` / `Right` arrow keys. Press `Esc` to close the application (automatically releasing the primary monitor cursor lock).
* **Build Requirements:** .NET 6.0 Desktop Runtime (`net6.0-windows`), Windows 10/11, NVIDIA GPU (`nvidia-smi` for GPU telemetry), and [Core Temp](https://www.alcpu.com/CoreTemp/) (optional, for CPU package temperature).

---

### To auto start the application set up a `Task Scheduler` with the below settings:
<img width="632" height="480" alt="image" src="https://github.com/user-attachments/assets/9d09e351-c9a5-47b6-b81d-a3eeefcf1a13" />

---

### In `Trigger` Tab, click `New`
<img width="591" height="516" alt="image" src="https://github.com/user-attachments/assets/2cd8c0cc-5347-47ae-b9f2-f4f82e1e70c5" />

---

### In `Action` Tab, click `New`
<img width="454" height="500" alt="image" src="https://github.com/user-attachments/assets/db9d3dcf-8ffd-4d49-9176-ad2d5f319c65" />

---

### `Conditions` Tab
<img width="632" height="480" alt="image" src="https://github.com/user-attachments/assets/47441484-0d68-4051-a871-7c7db80c25fd" />

---

### `Settings` Tab
<img width="632" height="480" alt="image" src="https://github.com/user-attachments/assets/49e70edb-656c-4d8d-92b9-45f6e21e8143" />

---

