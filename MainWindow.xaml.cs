using OBSController.Services;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Linq;
using FlashCap;
using System.Windows.Media.Imaging;

namespace OBSController
{
    public partial class MainWindow : Window
    {
        // P/Invoke per minimizzare finestre
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;

        private readonly OBSService _obs = new();

        // ─── Timers ────────────────────────────────────────────────────────────
        private DispatcherTimer _vcamStatusTimer;
        private DispatcherTimer _healthCheckTimer;
        private DispatcherTimer _reconnectTimer;

        // ─── Stato ────────────────────────────────────────────────────────────
        private bool _isConnected = false;
        private bool _isVCamActive = false;
        private bool _connectionPending = false;   // evita tentativi sovrapposti
        private DateTime _lastActivityTime = DateTime.Now;
        private string _lastOBSIp = "localhost";
        private int _lastOBSPort = 4455;
        private string _lastOBSPassword = "";

        // ─── FlashCap (preview Virtual Camera) ────────────────────────────────
        private CaptureDevice? _captureDevice;
        private DateTime _lastPreviewFrame = DateTime.MinValue;
        private const int PREVIEW_INTERVAL_MS = 50; // max ~20fps

        public MainWindow()
        {
            InitializeComponent();

            // Verifica aggiornamenti all'avvio
            _ = CheckForUpdatesAsync();

            // Timer controllo stato virtual camera
            _vcamStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _vcamStatusTimer.Tick += VCamStatusTimer_Tick;

            // Timer health check (monitor processi/rete)
            _healthCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _healthCheckTimer.Tick += HealthCheckTimer_Tick;
            _healthCheckTimer.Start();

            // Timer riconnessione automatica a OBS
            _reconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _reconnectTimer.Start();

            Loaded += (_, _) => LoadConfigurationDefaults();

            // Eventi OBS
            _obs.Connected += OnOBSConnected;
            _obs.Disconnected += OnOBSDisconnected;
            _obs.Error += (s, msg) => UpdateStatus("⚠ " + msg);
            _obs.Debug += (s, msg) => System.Diagnostics.Debug.WriteLine(msg);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PREVIEW — FlashCap (Virtual Camera OBS → Image WPF, ~20fps)
        // ═══════════════════════════════════════════════════════════════════

        private async void StartVlcPreview()   // nome mantenuto per non cambiare i call-site
        {
            if (_captureDevice != null) return;

            try
            {
                // Enumera i dispositivi DirectShow (sync)
                var descriptors = new CaptureDevices().EnumerateDescriptors().ToList();

                // Cerca la Virtual Camera di OBS
                var obsDevice = descriptors.FirstOrDefault(d =>
                    d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase) &&
                    d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    ?? descriptors.FirstOrDefault(d =>
                    d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase));

                if (obsDevice == null || obsDevice.Characteristics.Length == 0)
                {
                    // Log per debug
                    System.Diagnostics.Debug.WriteLine("[Preview] OBS Virtual Camera non trovata. Dispositivi disponibili:");
                    foreach (var d in descriptors)
                        System.Diagnostics.Debug.WriteLine($"  - {d.Name}");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[Preview] Trovata: {obsDevice.Name}");

                // Preferisci MJPEG (JPEG diretto, decodifica rapida), poi qualsiasi formato
                var characteristic = obsDevice.Characteristics
                    .Where(c => c.PixelFormat == FlashCap.PixelFormats.JPEG)
                    .OrderByDescending(c => c.Width)
                    .FirstOrDefault()
                    ?? obsDevice.Characteristics
                    .OrderByDescending(c => c.Width)
                    .FirstOrDefault();

                if (characteristic == null) return;

                System.Diagnostics.Debug.WriteLine($"[Preview] {characteristic.Width}x{characteristic.Height} {characteristic.PixelFormat} @{characteristic.FramesPerSecond}fps");

                // Apri dispositivo con callback async per i frame
                _captureDevice = await obsDevice.OpenAsync(characteristic, OnFrameArrived);
                await _captureDevice.StartAsync();

                Dispatcher.Invoke(() =>
                {
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    VCamOffPlaceholder.Visibility = Visibility.Collapsed;
                });

                System.Diagnostics.Debug.WriteLine("[Preview] Cattura avviata");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Preview Error] {ex.Message}");
            }
        }

        private async Task OnFrameArrived(PixelBufferScope bufferScope)
        {
            // Throttle a ~20fps per non sovraccaricare il thread di rendering WPF
            var now = DateTime.UtcNow;
            if ((now - _lastPreviewFrame).TotalMilliseconds < PREVIEW_INTERVAL_MS) return;
            _lastPreviewFrame = now;

            try
            {
                // CopyImage() è sincrono — restituisce i byte nel formato del dispositivo
                byte[] imageData = bufferScope.Buffer.CopyImage();

                // Crea BitmapImage (frozen = thread-safe per WPF)
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(imageData);
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();

                await Dispatcher.InvokeAsync(() =>
                {
                    PreviewImage.Source = bmp;
                }, System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Frame] {ex.Message}");
            }
        }

        private async void StopVlcPreview()   // nome mantenuto per non cambiare tutti i call-site
        {
            if (_captureDevice == null) return;

            try
            {
                await _captureDevice.StopAsync();
                _captureDevice.Dispose();
                _captureDevice = null;

                Dispatcher.Invoke(() =>
                {
                    PreviewImage.Source = null;
                    PreviewImage.Visibility = Visibility.Collapsed;
                });

                System.Diagnostics.Debug.WriteLine("[Preview] Cattura fermata");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Preview Stop] {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONFIGURAZIONE E AVVIO
        // ═══════════════════════════════════════════════════════════════════

        private async void LoadConfigurationDefaults()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (!File.Exists(configPath))
                {
                    UpdateStatus("appsettings.json non trovato");
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);

                string ip  = config["OBS"]?["IP"]?.ToString() ?? "localhost";
                int    port = config["OBS"]?["Port"]?.Value<int>() ?? 4455;
                string pwd  = config["OBS"]?["Password"]?.ToString() ?? "";

                // Salva credenziali subito (usate dal reconnect timer)
                _lastOBSIp       = ip;
                _lastOBSPort     = port;
                _lastOBSPassword = pwd;

                // Se OBS non è aperto, avvialo e aspetta (async, non blocca UI)
                if (!IsOBSRunning())
                {
                    UpdateStatus("⏳ Avvio OBS...");
                    bool started = TryStartOBS();
                    if (started)
                    {
                        // Aspetta che OBS carichi il WebSocket (max 8s, check ogni 500ms)
                        UpdateStatus("⏳ Attesa avvio OBS...");
                        await WaitForOBSReady(timeoutSeconds: 10);
                    }
                }

                // Singolo tentativo di connessione — il ReconnectTimer gestisce i retry
                TryConnect();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadConfig] {ex}");
                UpdateStatus($"Errore config: {ex.Message}");
            }
        }

        /// <summary>Aspetta che OBS sia avviato e il processo esista (non blocca UI).</summary>
        private async Task WaitForOBSReady(int timeoutSeconds)
        {
            int elapsed = 0;
            while (!IsOBSRunning() && elapsed < timeoutSeconds * 1000)
            {
                await Task.Delay(500);
                elapsed += 500;
            }
            // Piccola pausa extra per dare tempo al WebSocket di inizializzarsi
            await Task.Delay(1500);
        }

        /// <summary>Un singolo tentativo di connessione a OBS (non bloccante).</summary>
        private void TryConnect()
        {
            if (_isConnected || _connectionPending) return;

            _connectionPending = true;
            UpdateStatus($"⏳ Connessione a OBS ({_lastOBSIp}:{_lastOBSPort})...");

            try
            {
                _obs.Connect(_lastOBSIp, _lastOBSPort, _lastOBSPassword);
                // La connessione è asincrona: OnOBSConnected scatterà quando pronta
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TryConnect] {ex.Message}");
                _connectionPending = false;
                UpdateStatus("⚠ OBS non raggiungibile");
            }
        }

        private void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            // Salta se già connesso o se c'è già un tentativo in corso
            if (_isConnected || _connectionPending) return;

            System.Diagnostics.Debug.WriteLine("[ReconnectTimer] Tentativo riconnessione...");
            TryConnect();
        }

        private bool TryStartOBS()
        {
            try
            {
                string[] obsPaths = new[]
                {
                    "C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe",
                    "C:\\Program Files\\obs-studio\\bin\\64bit\\obs.exe",
                    "C:\\Program Files (x86)\\obs-studio\\bin\\32bit\\obs.exe",
                    "C:\\Program Files\\obs-studio\\obs64.exe",
                    "C:\\Program Files (x86)\\obs-studio\\obs.exe",
                };

                System.Diagnostics.Debug.WriteLine("[TryStartOBS] Ricerca OBS...");

                foreach (var path in obsPaths)
                {
                    System.Diagnostics.Debug.WriteLine($"[TryStartOBS] Verifica: {path}");
                    if (File.Exists(path))
                    {
                        System.Diagnostics.Debug.WriteLine($"[TryStartOBS] Trovato! Avvio: {path}");
                        var process = new Process();
                        process.StartInfo.FileName = path;
                        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(path);
                        process.StartInfo.UseShellExecute = true;
                        process.Start();
                        System.Diagnostics.Debug.WriteLine("[TryStartOBS] OBS avviato");
                        UpdateStatus("🔄 Avvio OBS in background...");

                        Task.Delay(800).ContinueWith(_ =>
                        {
                            try
                            {
                                // Cerca sia "obs64" che "obs"
                                var obsProcess = Process.GetProcessesByName("obs64").FirstOrDefault()
                                              ?? Process.GetProcessesByName("obs").FirstOrDefault();
                                if (obsProcess != null && obsProcess.MainWindowHandle != IntPtr.Zero)
                                {
                                    ShowWindow(obsProcess.MainWindowHandle, SW_MINIMIZE);
                                    System.Diagnostics.Debug.WriteLine("[TryStartOBS] OBS minimizzato");
                                }
                            }
                            catch { }
                        });

                        return true;
                    }
                }

                System.Diagnostics.Debug.WriteLine("[TryStartOBS] OBS non trovato in nessun percorso standard");
                UpdateStatus("⚠ OBS non trovato. Dove è installato?");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TryStartOBS Exception] {ex}");
                UpdateStatus($"⚠ Errore avvio OBS: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONNESSIONE OBS
        // ═══════════════════════════════════════════════════════════════════

        private void OnOBSConnected(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = true;
                _connectionPending = false;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                StatusText.Text = "Connesso";
                UpdateStatus("✓ Connesso a OBS");

                _vcamStatusTimer.Start();
                AutoStartVirtualCam();
            });
        }

        private void OnOBSDisconnected(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = false;
                _connectionPending = false;   // permetti nuovi tentativi al reconnect timer
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
                StatusText.Text = "Disconnesso";
                UpdateStatus("Disconnesso");

                StopVlcPreview();
                VCamOffPlaceholder.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;

                _vcamStatusTimer.Stop();
                UpdateVCamStatus(false);
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  SCENE
        // ═══════════════════════════════════════════════════════════════════

        private void BtnScene_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected) return;

            _lastActivityTime = DateTime.Now;
            var btn = sender as System.Windows.Controls.Button;
            string sceneName = btn?.Tag as string;

            if (string.IsNullOrEmpty(sceneName)) return;

            try
            {
                _obs.SetScene(sceneName);
                UpdateStatus($"Scena → {sceneName}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"⚠ Errore cambio scena: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONTROLLO VIRTUAL CAMERA
        // ═══════════════════════════════════════════════════════════════════

        private void VCamStatusTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isConnected) return;

            Task.Run(() =>
            {
                try
                {
                    bool isActive = _obs.GetVirtualCamStatus();
                    Dispatcher.Invoke(() => UpdateVCamStatus(isActive));
                }
                catch
                {
                    Dispatcher.Invoke(() => UpdateVCamStatus(false));
                }
            });
        }

        private void UpdateVCamStatus(bool isActive)
        {
            bool wasActive = _isVCamActive;
            _isVCamActive = isActive;

            // Gestisci cambio stato preview
            if (isActive && !wasActive)
            {
                // VCam appena attivata → avvia VLC (smooth 30fps)
                StartVlcPreview();
            }
            else if (!isActive && wasActive)
            {
                // VCam appena spenta → mostra messaggio "spenta"
                StopVlcPreview();
                VCamOffPlaceholder.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else if (!isActive && _isConnected && _captureDevice == null)
            {
                // VCam spenta al primo check dopo connessione → mostra messaggio
                VCamOffPlaceholder.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }

            // Aggiorna UI VCam
            if (isActive)
            {
                VCamStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                VCamStatusText.Text = "Accesa";
                VCamStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                VCamWarning.Visibility = Visibility.Collapsed;
                VCamActiveMsg.Visibility = Visibility.Visible;
                BtnToggleVCam.Visibility = Visibility.Collapsed;
            }
            else
            {
                VCamStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
                VCamStatusText.Text = "Spenta";
                VCamStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35));
                VCamWarning.Visibility = Visibility.Visible;
                VCamActiveMsg.Visibility = Visibility.Collapsed;
                BtnToggleVCam.Visibility = Visibility.Visible;
            }
        }

        private void AutoStartVirtualCam()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JObject.Parse(json);

                    bool autoStartVCam = config["Application"]?["AutoStartVirtualCam"]?.Value<bool>() ?? false;

                    if (autoStartVCam && !_isVCamActive)
                    {
                        _obs.StartVirtualCam();
                        UpdateStatus("✓ Webcam virtuale avviata automaticamente");
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"⚠ Errore avvio virtual camera: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  IMPOSTAZIONI
        // ═══════════════════════════════════════════════════════════════════

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            _lastActivityTime = DateTime.Now;
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        // ─── Toggle Virtual Camera ───────────────────────────────────
        private void BtnToggleVCam_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                UpdateStatus("⚠ Non connesso a OBS");
                return;
            }

            _lastActivityTime = DateTime.Now;

            try
            {
                _obs.StartVirtualCam();
                UpdateStatus("✓ Webcam virtuale avviata");
            }
            catch (Exception ex)
            {
                UpdateStatus($"⚠ Errore: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  MONITOR / HEALTH CHECK
        // ═══════════════════════════════════════════════════════════════════

        private void HealthCheckTimer_Tick(object? sender, EventArgs e)
        {
            _lastActivityTime = DateTime.Now;
            UpdateMonitorIndicators();
        }

        private void UpdateMonitorIndicators()
        {
            // Esegui i check in background per non bloccare l'UI (ping è bloccante)
            Task.Run(() =>
            {
                bool ptzActive = IsCameraActive("192.168.1.50");
                bool jwActive = IsProcessRunning("JWLibrary");
                bool zoomActive = IsZoomMeetingActive();
                bool onlytActive = IsProcessRunning("OnlyT");

                Dispatcher.Invoke(() =>
                {
                    UpdateMonitorColor(ChkPTZ, ptzActive);
                    UpdateMonitorColor(ChkJWLibrary, jwActive);
                    UpdateMonitorColor(ChkZoom, zoomActive);
                    UpdateMonitorColor(ChkOnlyt, onlytActive);
                });
            });
        }

        /// <summary>Controlla se OBS è in esecuzione (processo si chiama obs64 o obs).</summary>
        private bool IsOBSRunning()
        {
            try
            {
                return Process.GetProcessesByName("obs64").Length > 0
                    || Process.GetProcessesByName("obs").Length > 0;
            }
            catch { return false; }
        }

        private bool IsProcessRunning(string processName)
        {
            try { return Process.GetProcessesByName(processName).Length > 0; }
            catch { return false; }
        }

        private bool IsCameraActive(string ipAddress)
        {
            try
            {
                using var ping = new Ping();
                PingReply reply = ping.Send(ipAddress, 800);
                return reply?.Status == IPStatus.Success;
            }
            catch { return false; }
        }

        private bool IsZoomMeetingActive()
        {
            try
            {
                var zoomProcesses = Process.GetProcessesByName("zoom");
                if (zoomProcesses.Length == 0) return false;

                foreach (var process in zoomProcesses)
                {
                    try
                    {
                        string windowTitle = process.MainWindowTitle;
                        if (!string.IsNullOrEmpty(windowTitle) &&
                            (windowTitle.Contains("Meeting", StringComparison.OrdinalIgnoreCase) ||
                             windowTitle.Contains("Zoom", StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
                return false;
            }
            catch { return false; }
        }

        private void UpdateMonitorColor(System.Windows.Controls.CheckBox checkbox, bool isActive)
        {
            var template = checkbox?.Template;
            if (template == null) return;

            var circle = template.FindName("CheckCircle", checkbox) as System.Windows.Shapes.Ellipse;
            if (circle != null)
            {
                circle.Fill = isActive
                    ? new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47))
                    : new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));
                circle.Stroke = null;
                circle.StrokeThickness = 0;
                circle.Opacity = 1.0;
            }
        }

        private void UpdateStatus(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                _lastActivityTime = DateTime.Now;
                StatusText.Text = msg.Length > 50 ? msg.Substring(0, 47) + "..." : msg;
            });
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                string currentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString() ?? "0.1.0.0";

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "OBSController");
                string apiUrl = "https://api.github.com/repos/Dade00/khmanager/releases/latest";

                var response = await client.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var release = JObject.Parse(json);
                    string latestVersion = release["tag_name"]?.ToString().TrimStart('v') ?? "0.1.0";

                    if (IsNewerVersion(latestVersion, currentVersion))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Update] Nuova versione disponibile: {latestVersion}");
                        UpdateStatus($"⬆ Nuova versione disponibile: {latestVersion}. Scarica da GitHub!");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Update Check] {ex.Message}");
            }
        }

        private bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            var latest = new System.Version(latestVersion);
            var current = new System.Version(currentVersion);
            return latest > current;
        }

        protected override void OnClosed(EventArgs e)
        {
            // Ferma tutti i timer
            _vcamStatusTimer?.Stop();
            _healthCheckTimer?.Stop();
            _reconnectTimer?.Stop();

            // Ferma preview FlashCap
            if (_captureDevice != null)
            {
                try { _captureDevice.Dispose(); } catch { }
                _captureDevice = null;
            }

            // Spegni la videocamera virtuale
            try
            {
                if (_isVCamActive)
                    _obs.StopVirtualCam();
            }
            catch { }

            _obs.Disconnect();
            base.OnClosed(e);
        }
    }
}
