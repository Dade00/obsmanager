using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Types;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OBSController.Services
{
    /// <summary>
    /// Servizio che incapsula la comunicazione con OBS via WebSocket v5.
    /// Richiede OBS 28+ con il plugin WebSocket integrato abilitato.
    /// </summary>
    public class OBSService
    {
        // ─── OBS WebSocket client ────────────────────────────────────────────
        private readonly OBSWebsocket _obs;

        // ─── Eventi pubblici ─────────────────────────────────────────────────
        public event EventHandler? Connected;
        public event EventHandler? Disconnected;
        public event EventHandler<string>? SceneChanged;
        public event EventHandler<string>? Error;
        public event EventHandler<string>? Debug;  // Per tracciare la connessione

        // ─── Stato ───────────────────────────────────────────────────────────
        public bool IsConnected => _obs.IsConnected;

        // ─── Ctor ─────────────────────────────────────────────────────────────
        public OBSService()
        {
            _obs = new OBSWebsocket();

            _obs.Connected += (s, e) => Connected?.Invoke(this, EventArgs.Empty);

            _obs.Disconnected += (s, e) =>
                Disconnected?.Invoke(this, EventArgs.Empty);

            _obs.CurrentProgramSceneChanged += (s, e) =>
                SceneChanged?.Invoke(this, e.SceneName);
        }

        // ─── Connessione ──────────────────────────────────────────────────────

        /// <summary>Si connette a OBS via WebSocket.</summary>
        public void Connect(string ip, int port, string password)
        {
            try
            {
                Debug?.Invoke(this, $"[Connect] Tentativo ws://{ip}:{port}");
                _obs.ConnectAsync($"ws://{ip}:{port}", password);
                Debug?.Invoke(this, "[Connect] ConnectAsync chiamato");
            }
            catch (Exception ex)
            {
                Debug?.Invoke(this, $"[Connect Exception] {ex.GetType().Name}: {ex.Message}");
                Error?.Invoke(this, $"Connessione fallita: {ex.Message}");
            }
        }

        /// <summary>Disconnette da OBS.</summary>
        public void Disconnect()
        {
            try { _obs.Disconnect(); }
            catch { /* ignora errori in disconnessione */ }
        }

        // ─── Scene ────────────────────────────────────────────────────────────

        /// <summary>Restituisce la lista di tutte le scene.</summary>
        public List<SceneBasicInfo> GetScenes()
        {
            var info = _obs.GetSceneList();
            return info.Scenes;
        }

        /// <summary>Restituisce il nome della scena corrente del programma.</summary>
        public string GetCurrentScene()
        {
            return _obs.GetCurrentProgramScene();
        }

        /// <summary>Cambia la scena del programma.</summary>
        public void SetScene(string sceneName)
        {
            _obs.SetCurrentProgramScene(sceneName);
        }

        // ─── Screenshot / Anteprima ───────────────────────────────────────────

        /// <summary>
        /// Cattura uno screenshot della scena/sorgente specificata.
        /// Restituisce una stringa base64 data-URI (es. "data:image/jpeg;base64,...").
        /// Passa null come sourceName per usare la scena corrente del programma.
        /// </summary>
        public string? GetScreenshot(string? sourceName = null, int width = 1280, int height = 720)
        {
            try
            {
                var source = sourceName ?? _obs.GetCurrentProgramScene();
                // imageCompressionQuality: 70 = buon compromesso qualità/velocità
                return _obs.GetSourceScreenshot(source, "jpeg", width, height, 70);
            }
            catch
            {
                return null;
            }
        }

        // ─── Virtual Camera ───────────────────────────────────────────────────

        /// <summary>Restituisce true se la virtual camera è attiva.</summary>
        public bool GetVirtualCamStatus()
        {
            try
            {
                var status = _obs.GetVirtualCamStatus();
                // Controlla se la virtual camera è attiva
                // Prova le proprietà comuni: IsActive, Active, Enabled
                if (status == null) return false;

                var statusType = status.GetType();

                // Cerca la proprietà giusta
                var activeProperty = statusType.GetProperty("IsActive")
                    ?? statusType.GetProperty("Active")
                    ?? statusType.GetProperty("Enabled");

                if (activeProperty != null)
                {
                    return (bool)activeProperty.GetValue(status);
                }

                // Fallback: controlla ToString
                return status.ToString() != "Stopped";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Avvia la virtual camera.</summary>
        public void StartVirtualCam()
        {
            _obs.StartVirtualCam();
        }

        /// <summary>Ferma la virtual camera.</summary>
        public void StopVirtualCam()
        {
            _obs.StopVirtualCam();
        }

        /// <summary>Toggle automatico della virtual camera.</summary>
        public bool ToggleVirtualCam()
        {
            bool active = GetVirtualCamStatus();
            if (active) StopVirtualCam();
            else StartVirtualCam();
            return !active;
        }

        // ─── Invia immagine a sorgente OBS ────────────────────────────────────

        /// <summary>
        /// Aggiorna il path dell'immagine su una sorgente OBS di tipo "image_source".
        /// Crea la sorgente nella scena corrente se non esiste (opzionale).
        /// </summary>
        public void SetImageSourcePath(string inputName, string imagePath)
        {
            var settings = new JObject
            {
                ["file"] = imagePath,
                ["unload"] = false
            };
            _obs.SetInputSettings(inputName, settings);
        }

        /// <summary>
        /// Verifica se un input con il nome dato esiste tra gli input della scena.
        /// </summary>
        public bool InputExists(string inputName)
        {
            try
            {
                var inputs = _obs.GetInputList();
                return inputs.Exists(i =>
                    string.Equals(i.InputName, inputName, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
    }
}
