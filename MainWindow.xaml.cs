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
using LibVLCSharp.Shared;

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
        private DateTime _lastActivityTime = DateTime.Now;
        private string _lastOBSIp = "localhost";
        private int _lastOBSPort = 4455;
        private string _lastOBSPassword = "";

        // ─── LibVLC ────────────────────────────────────────────────────────────
        private LibVLC? _libVlc;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private bool _vlcPreviewActive = false;

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

            // Inizializza LibVLC dopo il caricamento della finestra
            Loaded += MainWindow_Loaded;

            // Eventi OBS
            _obs.Connected += OnOBSConnected;
            _obs.Disconnected += OnOBSDisconnected;
            _obs.Error += (s, msg) => UpdateStatus("⚠ " + msg);
            _obs.Debug += (s, msg) => System.Diagnostics.Debug.WriteLine(msg);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  INIT LIBVLC
        // ═══════════════════════════════════════════════════════════════════

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Inizializza LibVLC (usa i binari del pacchetto NuGet VideoLAN.LibVLC.Windows)
                _libVlc = new LibVLC(enableDebugLogs: false);
                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVlc);

                // Collega il MediaPlayer al VideoView WPF
                VlcVideoView.MediaPlayer = _mediaPlayer;

                System.Diagnostics.Debug.WriteLine("[VLC] LibVLC inizializzato correttamente");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VLC Init Error] {ex.Message}");
                // Se VLC non disponibile, funzionerà solo con fallback screenshot
            }

            // Avvia la configurazione dopo l'init VLC
            LoadConfigurationDefaults();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PREVIEW VLC (Virtual Camera attiva → 30fps fluido)
        // ═══════════════════════════════════════════════════════════════════

        private void StartVlcPreview()
        {
            if (_vlcPreviewActive) return;
            if (_mediaPlayer == null || _libVlc == null) return;

            try
            {
                var media = new Media(_libVlc, "dshow://", FromType.FromLocation);
                media.AddOption(":dshow-vdev=OBS Virtual Camera");
                media.AddOption(":dshow-adev=none");
                media.AddOption(":live-caching=50");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");

                _mediaPlayer.Play(media);
                _vlcPreviewActive = true;

                VlcVideoView.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                VCamOffPlaceholder.Visibility = Visibility.Collapsed;

                System.Diagnostics.Debug.WriteLine("[VLC] Preview avviato su OBS Virtual Camera");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VLC Preview Error] {ex.Message}");
            }
        }

        private void StopVlcPreview()
        {
            if (!_vlcPreviewActive) return;

            try
            {
                _mediaPlayer?.Stop();
                _vlcPreviewActive = false;
                VlcVideoView.Visibility = Visibility.Collapsed;
                System.Diagnostics.Debug.WriteLine("[VLC] Preview fermato");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VLC Stop Error] {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONFIGURAZIONE
        // ═══════════════════════════════════════════════════════════════════

        private void LoadConfigurationDefaults()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                System.Diagnostics.Debug.WriteLine($"[LoadConfig] Cercando: {configPath}");

                if (File.Exists(configPath))
                {
                    System.Diagnostics.Debug.WriteLine("[LoadConfig] File trovato!");
                    string json = File.ReadAllText(configPath);
                    var config = JObject.Parse(json);

                    string ip = config["OBS"]?["IP"]?.ToString() ?? "localhost";
                    int port = config["OBS"]?["Port"]?.Value<int>() ?? 4455;
                    string pwd = config["OBS"]?["Password"]?.ToString() ?? "";

                    System.Diagnostics.Debug.WriteLine($"[LoadConfig] Config: {ip}:{port}");

                    if (!IsProcessRunning("obs"))
                    {
                        System.Diagnostics.Debug.WriteLine("[LoadConfig] OBS non trovato, avvio automatico...");
                        UpdateStatus("OBS non trovato, avvio in background...");
                        if (TryStartOBS())
                        {
                            Task.Delay(4000).ContinueWith(_ =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateStatus("⏳ Connessione a OBS...");
                                    TryConnectWithRetry(ip, port, pwd, maxRetries: 5, delayMs: 800);
                                });
                            });
                            return;
                        }
                    }

                    ConnectToOBS(ip, port, pwd);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[LoadConfig] File NON trovato!");
                    UpdateStatus("appsettings.json non trovato");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadConfig Exception] {ex}");
                UpdateStatus($"Errore config: {ex.Message}");
            }
        }

        private void TryConnectWithRetry(string ip, int port, string password, int maxRetries = 5, int delayMs = 2000)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                System.Diagnostics.Debug.WriteLine($"[TryConnect] Tentativo {i + 1}/{maxRetries}");
                try
                {
                    _obs.Connect(ip, port, password);
                    System.Diagnostics.Debug.WriteLine("[TryConnect] ✓ Connesso!");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TryConnect] Errore tentativo {i + 1}: {ex.Message}");
                    if (i < maxRetries - 1)
                    {
                        UpdateStatus($"⏳ Retry {i + 1}/{maxRetries}...");
                        System.Threading.Thread.Sleep(delayMs);
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine("[TryConnect] ✗ Connessione fallita dopo tutti i tentativi");
            UpdateStatus("❌ Impossibile connettersi a OBS");
        }

        private void ConnectToOBS(string ip, int port, string password)
        {
            _lastOBSIp = ip;
            _lastOBSPort = port;
            _lastOBSPassword = password;

            try
            {
                UpdateStatus($"Tentativo connessione a ws://{ip}:{port}...");
                _obs.Connect(ip, port, password);
                UpdateStatus("⏳ Connessione in corso...");
            }
            catch (Exception ex)
            {
                UpdateStatus($"⚠ OBS non raggiungibile, tentativo avvio...");
                System.Diagnostics.Debug.WriteLine($"[OBS Connect Error] {ex}");

                if (TryStartOBS())
                {
                    Task.Delay(4000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try { _obs.Connect(ip, port, password); }
                            catch { }
                        });
                    });
                }
            }
        }

        private void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isConnected)
            {
                System.Diagnostics.Debug.WriteLine("[ReconnectTimer] Tentativo riconnessione...");
                try
                {
                    _obs.Connect(_lastOBSIp, _lastOBSPort, _lastOBSPassword);
                    System.Diagnostics.Debug.WriteLine("[ReconnectTimer] ✓ Riconnesso a OBS!");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ReconnectTimer] Riconnessione fallita: {ex.Message}");
                }
            }
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
                                var obsProcess = Process.GetProcessesByName("obs").FirstOrDefault();
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
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                StatusText.Text = "Connesso";
                UpdateStatus("✓ Connesso a OBS");

                // Avvia il check VCam (gestirà lui il preview corretto)
                _vcamStatusTimer.Start();

                // Avvia la virtual camera automaticamente se configurato
                AutoStartVirtualCam();
            });
        }

        private void OnOBSDisconnected(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = false;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
                StatusText.Text = "Disconnesso";
                UpdateStatus("Disconnesso");

                // Ferma preview VLC e mostra placeholder generico
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
            else if (!isActive && _isConnected && !_vlcPreviewActive)
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

            // Ferma VLC
            StopVlcPreview();
            _mediaPlayer?.Dispose();
            _libVlc?.Dispose();

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
