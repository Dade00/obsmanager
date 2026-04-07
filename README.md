# OBS Controller — WPF

App WPF minimalista per controllare OBS Studio via WebSocket v5.

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
4. Imposta una password (opzionale)

---

## Compilazione & Avvio

```bash
cd OBSController
dotnet restore
dotnet run
```

---

## Interfaccia

### Layout Principale

**Barra Superiore**
- Pulsante **⚙ Impostazioni** per configurare connessione e camera
- Indicatore di stato (rosso = disconnesso, verde = connesso)

**Pannello Sinistro**
- **4 Bottoni Scene**: Centrale, Doppio, Dimostrazione, Multimedia (click per switchare)
- **Status Checks**: Telecamera PTZ, JW Library, Zoom, Onlyt (read-only per ora)

**Area Destra**
- **Preview Webcam Virtuale**: 400x400px, aggiornamento in tempo reale della virtual camera di OBS

### Impostazioni

Accedi tramite pulsante **⚙ Impostazioni** per configurare:
- IP, Porta, Password OBS WebSocket
- IP Camera PTZ
- Stream RTSP della camera
- Endpoint go2rtc

---

## Funzionalità

✅ Connessione automatica a OBS (da appsettings.json)  
✅ Cambio scene con 4 pulsanti dedicati  
✅ Anteprima webcam virtuale fluida (max 400x400px)  
✅ Pannello impostazioni per configurazione completa  

### In Sviluppo

🔄 Integrazione go2rtc per stream RTSP dalla camera PTZ  
🔄 Controllo virtual camera (play/stop)  
🔄 Status checks sincronizzati con sorgenti OBS  

---

## Architettura

```
OBSController/
├── OBSController.csproj          # Progetto .NET 8 WPF
├── App.xaml / App.xaml.cs        # Entry point
├── MainWindow.xaml               # UI principale semplificata
├── MainWindow.xaml.cs            # Logica UI
├── SettingsWindow.xaml           # UI impostazioni
├── SettingsWindow.xaml.cs        # Logica impostazioni
├── appsettings.json              # Configurazione
└── Services/
    └── OBSService.cs             # Wrapper OBSWebsocketDotNet
```

### Dipendenze NuGet
- `obs-websocket-dotnet` 5.0.1 — WebSocket v5
- `Newtonsoft.Json` 13.0.3 — Configurazione JSON

---

## Note

- L'anteprima viene catturata dalla scena Program di OBS
- Configurazione caricata da `appsettings.json` al caricamento
- Per aggiungere una nuova scena ai bottoni, modificare il Tag dei button in MainWindow.xaml

---
