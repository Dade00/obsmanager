# OBS Controller — WPF

App WPF per controllare OBS Studio via WebSocket v5.

---

## Requisiti

| Componente | Versione minima |
|---|---|
| OBS Studio | **28+** (WebSocket v5 integrato) |
| .NET SDK | **8.0** |
| Windows | 10 / 11 |

---

## Setup OBS

1. Apri OBS → **Strumenti → WebSocket Server Settings**
2. Spunta **Enable WebSocket Server**
3. Porta default: `4455`
4. Imposta una password (opzionale ma consigliato)

---

## Compilazione & Avvio

```bash
cd OBSController
dotnet restore
dotnet run
```

Oppure compila in Release:

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## Funzionalità

### Connessione
- IP + Porta + Password → pulsante **Connetti**
- Indicatore di stato (rosso = disconnesso, verde = connesso)

### Scene
- Lista completa delle scene OBS
- Click su una voce → switch immediato
- La selezione si aggiorna se la scena cambia da OBS

### Anteprima
- Screenshot della scena Program aggiornato a intervalli configurabili (0.5 / 1 / 2 / 5 secondi)
- Mostra FPS reali dell'anteprima
- Basato su `GetSourceScreenshot` (JPEG, 70% qualità)

### Virtual Camera
- Indicatore visivo attiva/inattiva
- Pulsante toggle start/stop

### Invia Immagine
- Seleziona qualsiasi immagine (PNG, JPG, BMP, GIF, WebP)
- Specifica il nome di una sorgente OBS di tipo **`image_source`**
- Clic su **Invia** → aggiorna il file nella sorgente OBS in tempo reale

> **Come creare la sorgente in OBS:**
> Sources → "+" → Image → nome esatto che hai scritto nel campo "Sorgente OBS"

---

## Architettura

```
OBSController/
├── OBSController.csproj       # Progetto .NET 8 WPF
├── App.xaml / App.xaml.cs     # Entry point
├── MainWindow.xaml            # UI (dark broadcast theme)
├── MainWindow.xaml.cs         # Logica UI
└── Services/
    └── OBSService.cs          # Wrapper OBSWebsocketDotNet
```

### Dipendenze NuGet
- `OBSWebsocketDotNet` 5.0.0 — wrapper WebSocket v5
- `Newtonsoft.Json` 13.0.3 — serializzazione settings sorgente

---

## Estensioni possibili

- **NDI preview** — fluido 30/60fps (richiede plugin OBS NDI + NuGet NewTek NDI SDK)
- **Audio mixer** — controllo volume input/output via `GetInputVolume` / `SetInputVolume`
- **Hotkey globali** — `RegisterHotKey` Win32 per cambiare scena da tastiera
- **Tray icon** — controllo da system tray senza finestra aperta
- **Multi-output** — gestione più istanze OBS via tab

---

## Note

- L'anteprima via screenshot non è fluida (max ~2fps utili); per anteprima video reale usare NDI
- `SetImageSourcePath` richiede che la sorgente esista già in OBS come `image_source`
- Il percorso immagine deve essere assoluto e accessibile da OBS (non usare path di rete non mappati)
