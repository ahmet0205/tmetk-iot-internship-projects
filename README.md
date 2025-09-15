# Manufacturing Belt Simulator (StereoKit + MQTT)

A real-time manufacturing belt simulation built with **StereoKit 0.3.11** and **MQTTnet**.  
Vehicles are spawned from MQTT messages, dynamically recolored, and move along a conveyor belt.  
HUD overlays display counters, FPS, and the latest vehicle data. The scene includes background models and audio.

---

## ✨ Features
- **Real-time data**: Subscribes to `IOT252/#` via MQTT.
- **Model selection**: Corolla or C-HR decided by `carFamily/katashiki`.
- **Dynamic recoloring**: Uses `ColorMap` with material ID matching for body/roof/secondary parts.
- **Queue logic**: Cars spawn instantly (first car at `t=0`), maintain spacing, and are removed when reaching the end.
- **HUD**: Displays number of cars, incoming count, last vehicle data, and FPS.
- **Environment**: Conveyor belt, station labels (`Kaput`, `Far`, `Tampon`), and static background (`harita.glb`).
- **Audio**: Looping background sound (`bg.mp3`).
- **Controls**: 
  - Keyboard: `WASD` + `Q/E` for movement.  
  - Controller: Left-hand `X` button (`IsX1Pressed`) to move in sync with the belt.

---

## 🛠 Requirements
- Windows + .NET (tested with `net9.0`).
- [StereoKit 0.3.11](https://stereokit.net)
- [MQTTnet](https://github.com/dotnet/MQTTnet) (>= 4.3.x)
- (Optional) Meta Quest 2 with Oculus Link/Air Link for VR testing.

Assets must exist in the `Assets/` folder:
- `harita.glb`  
- `bg.mp3`  
- Corolla parts: `beyazpre.glb`, `beyazkaput.glb`, `beyazfar.glb`, `beyaztampon.glb`  
- C-HR parts: `chrPre.glb`, `chrKaput.glb`, `chrFar.glb`, `chrTampon.glb`

---

## ⚙️ Setup
1. Clone the repo and restore dependencies:
   ```bash
   dotnet restore
   ```
2. Add assets to the `Assets/` folder (filenames must match the code).
3. Add packages if missing:
   ```bash
   dotnet add package StereoKit --version 0.3.11
   dotnet add package MQTTnet
   ```

---

## 🔧 Configuration
At the top of `Program.cs`, configure MQTT broker details:

```csharp
const string MQTT_HOST = "10.116.116.20";
const int    MQTT_PORT = 7000;
const string MQTT_CARS_TOPIC = "IOT252/#";
```

---

## ▶️ Run
```bash
dotnet run
```

- By default, the app runs in **Flatscreen** mode (`DisplayMode.Flatscreen`).  
- For VR, enable Oculus Link/Air Link and run with your Quest 2 connected.

---

## 📡 MQTT Message Schema
**Topic**: `IOT252/#`  
**Payload (JSON)**:
```json
{
  "bodyNo": 37365,
  "katashiki": "ZWE219L-DEXNBW",
  "colorExtCode": "1K6",
  "vinNo": "NMTBD3BE90Rxxxxxx",
  "carFamily": "x94W",
  "loDate": "20250911"
}
```

- `carFamily`: `"x94W"` = Corolla, `"x00W"` = C-HR  
- `colorExtCode`: mapped to RGB values in `ColorMap`
