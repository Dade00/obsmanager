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
        private FlashCap.VideoCharacteristics? _captureChar; // formato/dimensione del dispositivo
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

        private async void StartVlcPreview()
        {
            if (_captureDevice != null) return;

            try
            {
                var descriptors = new CaptureDevices().EnumerateDescriptors().ToList();

                // Cerca OBS Virtual Camera
                var obsDevice = descriptors.FirstOrDefault(d =>
                    d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase) &&
                    d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    ?? descriptors.FirstOrDefault(d =>
                    d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase));

                if (obsDevice == null || obsDevice.Characteristics.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Preview] OBS Virtual Camera non trovata. Dispositivi:");
                    foreach (var d in descriptors)
                        System.Diagnostics.Debug.WriteLine($"  - [{d.Name}]");
                    return;
                }

                // Prendi la risoluzione più bassa disponibile per leggerezza (la preview è piccola)
                var characteristic = obsDevice.Characteristics
                    .OrderBy(c => c.Width)
                    .FirstOrDefault() ?? obsDevice.Characteristics[0];

                System.Diagnostics.Debug.WriteLine($"[Preview] {obsDevice.Name} → {characteristic.Width}x{characteristic.Height} {characteristic.PixelFormat}");

                _captureChar = characteristic;
                _captureDevice = await obsDevice.OpenAsync(characteristic, OnFrameArrived);
                await _captureDevice.StartAsync();

                Dispatcher.Invoke(() =>
                {
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    VCamOffPlaceholder.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Preview Error] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task OnFrameArrived(PixelBufferScope bufferScope)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPreviewFrame).TotalMilliseconds < PREVIEW_INTERVAL_MS) return;
            _lastPreviewFrame = now;

            try
            {
                byte[] data = bufferScope.Buffer.CopyImage();
                if (data.Length < 4) return;

                var fmt = _captureChar?.PixelFormat ?? FlashCap.PixelFormats.Unknown;
                int w = _captureChar?.Width ?? 0;
                int h = _captureChar?.Height ?? 0;

                BitmapSource? bmp = null;

                if (fmt == FlashCap.PixelFormats.JPEG || (data[0] == 0xFF && data[1] == 0xD8))
                {
                    // JPEG: decodifica diretta
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(data);
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.EndInit();
                    bi.Freeze();
                    bmp = bi;
                }
                else if (fmt == FlashCap.PixelFormats.NV12 && w > 0 && h > 0)
                {
                    bmp = NV12ToBitmapSource(data, w, h);
                }
                else if ((fmt == FlashCap.PixelFormats.YUYV || fmt == FlashCap.PixelFormats.UYVY) && w > 0 && h > 0)
                {
                    bmp = YUY2ToBitmapSource(data, w, h, fmt == FlashCap.PixelFormats.UYVY);
                }
                else if (w > 0 && h > 0)
                {
                    // Tentativo generico: prova come JPEG, poi come RGB24
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = new MemoryStream(data);
                        bi.EndInit();
                        bi.Freeze();
                        bmp = bi;
                    }
                    catch
                    {
                        System.Diagnostics.Debug.WriteLine($"[Frame] Formato sconosciuto: {fmt}, {data.Length} bytes");
                    }
                }

                if (bmp == null) return;

                await Dispatcher.InvokeAsync(() => PreviewImage.Source = bmp,
                    System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Frame Error] {ex.Message}");
            }
        }

        /// <summary>Converte NV12 (YUV 4:2:0 semi-planar) in Bgra32 per WPF.</summary>
        private static BitmapSource NV12ToBitmapSource(byte[] data, int w, int h)
        {
            int ySize = w * h;
            var px = new byte[ySize * 4];

            for (int y = 0; y < h; y++)
            {
                int uvBase = ySize + (y >> 1) * w;
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int yv = data[rowOff + x];
                    int uvOff = uvBase + (x & ~1);
                    int u = data[uvOff] - 128;
                    int v = data[uvOff + 1] - 128;

                    int r = Math.Clamp((int)(yv + 1.402f * v),        0, 255);
                    int g = Math.Clamp((int)(yv - 0.344f * u - 0.714f * v), 0, 255);
                    int b = Math.Clamp((int)(yv + 1.772f * u),        0, 255);

                    int i = (rowOff + x) * 4;
                    px[i] = (byte)b; px[i+1] = (byte)g; px[i+2] = (byte)r; px[i+3] = 255;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Converte YUY2/UYVY (YUV 4:2:2 packed) in Bgra32 per WPF.</summary>
        private static BitmapSource YUY2ToBitmapSource(byte[] data, int w, int h, bool isUYVY)
        {
            var px = new byte[w * h * 4];

            for (int y = 0; y < h; y++)
            {
                int rowSrc = y * w * 2;
                int rowDst = y * w * 4;
                for (int x = 0; x < w; x += 2)
                {
                    int s = rowSrc + x * 2;
                    int y0, u, y1, v;
                    if (isUYVY) { u = data[s]-128; y0 = data[s+1]; v = data[s+2]-128; y1 = data[s+3]; }
                    else        { y0 = data[s]; u = data[s+1]-128; y1 = data[s+2]; v = data[s+3]-128; }

                    for (int p = 0; p < 2; p++)
                    {
                        int yv = (p == 0) ? y0 : y1;
                        int r = Math.Clamp((int)(yv + 1.402f * v),             0, 255);
                        int g = Math.Clamp((int)(yv - 0.344f * u - 0.714f * v), 0, 255);
                        int b = Math.Clamp((int)(yv + 1.772f * u),             0, 255);
                        int i = rowDst + (x + p) * 4;
                        px[i] = (byte)b; px[i+1] = (byte)g; px[i+2] = (byte)r; px[i+3] = 255;
                    }
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
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
                                var obsProcess = Process.GetProcessesByName("obs64").FirstOrDefault();
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

        /// <summary>Controlla se OBS Studio è in esecuzione (obs64.exe).</summary>
        private bool IsOBSRunning()
        {
            try
            {
                // Controlla SOLO obs64 (il vero eseguibile di OBS Studio 64-bit)
                // Evita falsi positivi da processi "obs" generici o helper
                var procs = Process.GetProcessesByName("obs64");
                return procs.Length > 0 && procs.Any(p => !p.HasExited);
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
