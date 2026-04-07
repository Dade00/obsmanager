using OBSController.Services;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using System.IO;

namespace OBSController
{
    public partial class MainWindow : Window
    {
        private readonly OBSService _obs = new();
        private DispatcherTimer _previewTimer;
        private DispatcherTimer _vcamStatusTimer;
        private int _previewIntervalMs = 1000;
        private bool _isConnected = false;
        private bool _isVCamActive = false;

        public MainWindow()
        {
            InitializeComponent();
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

            // Eventi OBS
            _obs.Connected += OnOBSConnected;
            _obs.Disconnected += OnOBSDisconnected;
            _obs.Error += (s, msg) => UpdateStatus("⚠ " + msg);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONFIGURAZIONE
        // ═══════════════════════════════════════════════════════════════════

        private void LoadConfigurationDefaults()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JObject.Parse(json);

                    // Lettura configurazione automatica al caricamento
                    string ip = config["OBS"]?["IP"]?.ToString() ?? "localhost";
                    int port = config["OBS"]?["Port"]?.Value<int>() ?? 4455;
                    string pwd = config["OBS"]?["Password"]?.ToString() ?? "";

                    // Connessione automatica
                    ConnectToOBS(ip, port, pwd);
                }
            }
            catch
            {
                UpdateStatus("Configurazione non trovata, connessione manuale richiesta");
            }
        }

        private void ConnectToOBS(string ip, int port, string password)
        {
            try
            {
                _obs.Connect(ip, port, password);
                UpdateStatus("Connessione in corso...");
            }
            catch (Exception ex)
            {
                UpdateStatus("⚠ Errore connessione: " + ex.Message);
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
                VCamStatusText.Text = "Webcam Virtuale: Accesa";
                VCamStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                VCamWarning.Visibility = Visibility.Collapsed;
            }
            else
            {
                VCamStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
                VCamStatusText.Text = "Webcam Virtuale: Spenta";
                VCamStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x33, 0x33));
                VCamWarning.Visibility = Visibility.Visible;
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

            try
            {
                // Prendi screenshot della scena corrente (ricorda: è la webcam virtuale)
                string? dataUri = _obs.GetScreenshot(null, 400, 400);
                if (dataUri is null) return;

                // Converti data-URI → BitmapImage
                string base64 = dataUri.Contains(",")
                    ? dataUri[(dataUri.IndexOf(',') + 1)..]
                    : dataUri;

                byte[] imageBytes = Convert.FromBase64String(base64);
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(imageBytes);
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.None;
                bmp.EndInit();
                bmp.Freeze();

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
                    UpdateStatus($"⚠ Anteprima non disponibile: {ex.Message}");
                });
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  IMPOSTAZIONI
        // ═══════════════════════════════════════════════════════════════════

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Apri finestra impostazioni
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UTILITY
        // ═══════════════════════════════════════════════════════════════════

        private void UpdateStatus(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = msg.Length > 50 ? msg.Substring(0, 47) + "..." : msg;
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _previewTimer?.Stop();
            _vcamStatusTimer?.Stop();
            _obs.Disconnect();
            base.OnClosed(e);
        }
    }
}
