using OBSController.Services;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace OBSController
{
    public partial class MainWindow : Window
    {
        // P/Invoke per minimizzare finestre
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;

        private readonly OBSService _obs = new();
        private DispatcherTimer _previewTimer;
        private DispatcherTimer _vcamStatusTimer;
        private DispatcherTimer _healthCheckTimer;
        private DispatcherTimer _reconnectTimer; // Timer per riconnessione automatica
        private int _previewIntervalMs = 40; // 25 FPS per fluidità video
        private bool _isConnected = false;
        private bool _isVCamActive = false;
        private DateTime _lastActivityTime = DateTime.Now;
        private const int FREEZE_TIMEOUT_MS = 5000; // 5 secondi prima di considerare l'app freezata
        private string _lastOBSIp = "localhost";
        private int _lastOBSPort = 4455;
        private string _lastOBSPassword = "";

        public MainWindow()
        {
            InitializeComponent();

            // Verifica aggiornamenti all'avvio
            _ = CheckForUpdatesAsync();

            LoadConfigurationDefaults();

            // Timer anteprima webcam virtuale
            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_previewIntervalMs)
            };
            _previewTimer.Tick += PreviewTimer_Tick;

            // Timer controllo stato virtual camera
            _vcamStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _vcamStatusTimer.Tick += VCamStatusTimer_Tick;

            // Timer controllo health dell'app (freezata)
            _healthCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _healthCheckTimer.Tick += HealthCheckTimer_Tick;
            _healthCheckTimer.Start();

            // Timer riconnessione automatica a OBS
            _reconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5) // Prova ogni 5 secondi se disconnesso
            };
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _reconnectTimer.Start();

            // Eventi OBS
            _obs.Connected += OnOBSConnected;
            _obs.Disconnected += OnOBSDisconnected;
            _obs.Error += (s, msg) => UpdateStatus("⚠ " + msg);
            _obs.Debug += (s, msg) => System.Diagnostics.Debug.WriteLine(msg);  // Log nella finestra Output
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

                    // Lettura configurazione automatica al caricamento
                    string ip = config["OBS"]?["IP"]?.ToString() ?? "localhost";
                    int port = config["OBS"]?["Port"]?.Value<int>() ?? 4455;
                    string pwd = config["OBS"]?["Password"]?.ToString() ?? "";

                    System.Diagnostics.Debug.WriteLine($"[LoadConfig] Config: {ip}:{port}");

                    // Verifica se OBS è già in esecuzione
                    if (!IsProcessRunning("obs"))
                    {
                        System.Diagnostics.Debug.WriteLine("[LoadConfig] OBS non trovato, avvio automatico...");
                        UpdateStatus("OBS non trovato, avvio in background...");
                        if (TryStartOBS())
                        {
                            // Aspetta che OBS si avvii (4 secondi), poi connetti con retry
                            Task.Delay(4000).ContinueWith(_ =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateStatus("⏳ Connessione a OBS...");
                                    TryConnectWithRetry(ip, port, pwd, maxRetries: 5, delayMs: 800);
                                });
                            });
                            return; // Esce senza connettere subito
                        }
                    }

                    // Connessione automatica (se OBS è già in esecuzione)
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

            // Se tutti i tentativi falliscono
            System.Diagnostics.Debug.WriteLine("[TryConnect] ✗ Connessione fallita dopo tutti i tentativi");
            UpdateStatus("❌ Impossibile connettersi a OBS");
        }

        private void ConnectToOBS(string ip, int port, string password)
        {
            // Salva le credenziali per riconnessioni automatiche
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

                // Prova ad avviare OBS se non è aperto
                if (TryStartOBS())
                {
                    // Riprova la connessione dopo 4 secondi
                    Task.Delay(4000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                _obs.Connect(ip, port, password);
                            }
                            catch { }
                        });
                    });
                }
            }
        }

        private void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            // Se disconnesso, prova a riconnettersi
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
                // Percorsi comuni di OBS - ordine di preferenza
                string[] obsPaths = new[]
                {
                    "C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe", // 64-bit (nome corretto)
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

                        // Minimizza subito (entro 1 secondo)
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
                _previewTimer.Start();
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
                PreviewImage.Source = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                UpdateStatus("Disconnesso");
                _previewTimer.Stop();
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

            try
            {
                bool isActive = _obs.GetVirtualCamStatus();
                Dispatcher.Invoke(() => UpdateVCamStatus(isActive));
            }
            catch
            {
                // Se errore nel controllo, considera spenta
                Dispatcher.Invoke(() => UpdateVCamStatus(false));
            }
        }

        private void UpdateVCamStatus(bool isActive)
        {
            _isVCamActive = isActive;

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
        //  ANTEPRIMA WEBCAM VIRTUALE
        // ═══════════════════════════════════════════════════════════════════

        private void PreviewTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isConnected) return;

            // Esegui il caricamento in thread separato per non bloccare l'UI
            Task.Run(() =>
            {
                try
                {
                    // Prendi screenshot con lo stesso aspect ratio della preview (16:9)
                    string? dataUri = _obs.GetScreenshot(null, 410, 230);
                    if (dataUri is null) return;

                    // Converti data-URI → BitmapImage in background thread
                    string base64 = dataUri.Contains(",")
                        ? dataUri[(dataUri.IndexOf(',') + 1)..]
                        : dataUri;

                    byte[] imageBytes = Convert.FromBase64String(base64);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(imageBytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.None;
                    bmp.EndInit();
                    bmp.Freeze();

                    // Aggiorna UI nel thread principale
                    Dispatcher.Invoke(() =>
                    {
                        PreviewImage.Source = bmp;
                        PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus($"⚠ Preview: {ex.Message}");
                    });
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  IMPOSTAZIONI
        // ═══════════════════════════════════════════════════════════════════

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            _lastActivityTime = DateTime.Now;
            // Apri finestra impostazioni
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
        //  UTILITY
        // ═══════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════
        //  MONITOR HEALTH CHECK
        // ═══════════════════════════════════════════════════════════════════

        private void HealthCheckTimer_Tick(object? sender, EventArgs e)
        {
            _lastActivityTime = DateTime.Now;
            UpdateMonitorIndicators();
        }

        private void UpdateMonitorIndicators()
        {
            Dispatcher.Invoke(() =>
            {
                // Controlla lo stato di ciascun servizio/processo
                bool ptzActive = IsCameraActive("192.168.1.50");
                bool jwActive = IsProcessRunning("JWLibrary");
                bool zoomActive = IsZoomMeetingActive(); // Controlla riunione attiva, non solo processo
                bool onlytActive = IsProcessRunning("OnlyT");

                // Aggiorna gli indicatori con i loro stati specifici
                UpdateMonitorColor(ChkPTZ, ptzActive);
                UpdateMonitorColor(ChkJWLibrary, jwActive);
                UpdateMonitorColor(ChkZoom, zoomActive);
                UpdateMonitorColor(ChkOnlyt, onlytActive);
            });
        }

        /// <summary>Verifica se un processo è in esecuzione</summary>
        private bool IsProcessRunning(string processName)
        {
            try
            {
                return Process.GetProcessesByName(processName).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Verifica se la telecamera è raggiungibile via ping</summary>
        private bool IsCameraActive(string ipAddress)
        {
            try
            {
                using (var ping = new Ping())
                {
                    PingReply reply = ping.Send(ipAddress, 1000); // timeout 1 secondo
                    return reply?.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Verifica se c'è una riunione Zoom attiva</summary>
        private bool IsZoomMeetingActive()
        {
            try
            {
                // Verifica se il processo Zoom è in esecuzione
                var zoomProcesses = Process.GetProcessesByName("zoom");
                if (zoomProcesses.Length == 0) return false;

                // Cerca finestre Zoom che indicano una riunione attiva
                foreach (var process in zoomProcesses)
                {
                    try
                    {
                        string windowTitle = process.MainWindowTitle;
                        // Indica riunione attiva: titolo contiene "Meeting" o numero ID riunione
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
            catch
            {
                return false;
            }
        }

        private void UpdateMonitorColor(System.Windows.Controls.CheckBox checkbox, bool isActive)
        {
            // Estrai il template e modifica il colore del cerchio
            var template = checkbox?.Template;
            if (template == null) return;

            var circle = template.FindName("CheckCircle", checkbox) as System.Windows.Shapes.Ellipse;
            if (circle != null)
            {
                if (isActive)
                {
                    // 🟢 Verde = Attivo
                    circle.Fill = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                    circle.Stroke = null;
                    circle.StrokeThickness = 0;
                    circle.Opacity = 1.0;
                }
                else
                {
                    // 🔴 Rosso = Inattivo
                    circle.Fill = new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));
                    circle.Stroke = null;
                    circle.StrokeThickness = 0;
                    circle.Opacity = 1.0;
                }
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
                // Versione corrente dell'app
                string currentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString() ?? "0.1.0.0";

                // Verifica la versione più recente su GitHub API
                using (var client = new System.Net.Http.HttpClient())
                {
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
            _previewTimer?.Stop();
            _vcamStatusTimer?.Stop();
            _healthCheckTimer?.Stop();

            // Spegni la videocamera virtuale prima di disconnettere
            try
            {
                if (_isVCamActive)
                {
                    _obs.StopVirtualCam();
                }
            }
            catch { /* ignora errori */ }

            _obs.Disconnect();
            base.OnClosed(e);
        }
    }
}
