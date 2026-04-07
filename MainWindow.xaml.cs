using Microsoft.Win32;
using OBSController.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace OBSController
{
    public partial class MainWindow : Window
    {
        // ─── Servizio OBS ────────────────────────────────────────────────────
        private readonly OBSService _obs = new();

        // ─── Timer anteprima ─────────────────────────────────────────────────
        private readonly DispatcherTimer _previewTimer;
        private int _previewIntervalMs = 1000;

        // ─── FPS counter ─────────────────────────────────────────────────────
        private readonly Stopwatch _fpsWatch = new();
        private int _frameCount = 0;

        // ─── Stato interno ────────────────────────────────────────────────────
        private bool _isConnected = false;
        private bool _isVCamActive = false;
        private string _selectedImagePath = string.Empty;

        // ─── Ctor ─────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            LoadConfigurationDefaults();

            // Collegamento eventi OBS
            _obs.Connected    += OnOBSConnected;
            _obs.Disconnected += OnOBSDisconnected;
            _obs.SceneChanged += OnSceneChanged;
            _obs.Error        += (s, msg) => SetStatus($"⚠  {msg}");

            // Timer anteprima
            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_previewIntervalMs)
            };
            _previewTimer.Tick += PreviewTimer_Tick;

            // Timer FPS display
            var fpsDisplayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            fpsDisplayTimer.Tick += (s, e) =>
            {
                if (_isConnected)
                {
                    double fps = _fpsWatch.IsRunning
                        ? _frameCount / _fpsWatch.Elapsed.TotalSeconds
                        : 0;
                    TxtFps.Text = $"{fps:F1} fps";
                    _frameCount = 0;
                    _fpsWatch.Restart();
                }
            };
            fpsDisplayTimer.Start();
            _fpsWatch.Start();

            // Timestamp nella status bar
            var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (s, e) =>
                TxtTimestamp.Text = DateTime.Now.ToString("HH:mm:ss");
            clockTimer.Start();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONNESSIONE
        // ═══════════════════════════════════════════════════════════════════

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                _obs.Disconnect();
                return;
            }

            if (!int.TryParse(TxtPort.Text.Trim(), out int port))
            {
                SetStatus("⚠  Porta non valida");
                return;
            }

            string ip  = TxtIP.Text.Trim();
            string pwd = TxtPassword.Password;

            SetStatus($"Connessione a {ip}:{port}...");
            BtnConnect.IsEnabled = false;
            _obs.Connect(ip, port, pwd);
        }

        private void OnOBSConnected(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = true;

                // UI connessa
                BtnConnect.Content    = "Disconnetti";
                BtnConnect.IsEnabled  = true;
                BtnVirtualCam.IsEnabled = true;
                BtnSendImage.IsEnabled = !string.IsNullOrEmpty(_selectedImagePath);
                StatusDot.Fill        = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
                StatusDot.Effect      = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0x43, 0xA0, 0x47),
                    BlurRadius = 8, ShadowDepth = 0
                };
                PreviewPlaceholder.Visibility = Visibility.Collapsed;

                SetStatus("Connesso a OBS");
                LoadScenes();
                RefreshVCamStatus();

                _previewTimer.Interval = TimeSpan.FromMilliseconds(_previewIntervalMs);
                _previewTimer.Start();
                _frameCount = 0;
                _fpsWatch.Restart();
            });
        }

        private void OnOBSDisconnected(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = false;

                // UI disconnessa
                BtnConnect.Content    = "Connetti";
                BtnConnect.IsEnabled  = true;
                BtnVirtualCam.IsEnabled = false;
                BtnSendImage.IsEnabled  = false;
                StatusDot.Fill        = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                StatusDot.Effect      = null;
                PreviewImage.Source   = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                TxtCurrentScene.Text  = string.Empty;
                TxtFps.Text           = "— fps";
                SceneListBox.ItemsSource = null;
                SetVCamUI(false);

                _previewTimer.Stop();
                SetStatus("Disconnesso");
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  SCENE
        // ═══════════════════════════════════════════════════════════════════

        private void LoadScenes()
        {
            try
            {
                var scenes       = _obs.GetScenes();
                var currentScene = _obs.GetCurrentScene();

                SceneListBox.ItemsSource = scenes;
                SceneListBox.DisplayMemberPath = "Name";

                // Seleziona la scena corrente senza triggherare il cambio
                SceneListBox.SelectionChanged -= SceneListBox_SelectionChanged;
                foreach (var item in SceneListBox.Items)
                {
                    if (item is OBSWebsocketDotNet.Types.SceneBasicInfo info
                        && info.Name == currentScene)
                    {
                        SceneListBox.SelectedItem = item;
                        break;
                    }
                }
                SceneListBox.SelectionChanged += SceneListBox_SelectionChanged;

                TxtCurrentScene.Text = currentScene;
            }
            catch (Exception ex)
            {
                SetStatus($"⚠  Errore caricamento scene: {ex.Message}");
            }
        }

        private void SceneListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isConnected) return;
            if (SceneListBox.SelectedItem is not OBSWebsocketDotNet.Types.SceneBasicInfo scene) return;

            try
            {
                _obs.SetScene(scene.Name);
                TxtCurrentScene.Text = scene.Name;
                SetStatus($"Scena → {scene.Name}");
            }
            catch (Exception ex)
            {
                SetStatus($"⚠  Cambio scena fallito: {ex.Message}");
            }
        }

        private void OnSceneChanged(object? sender, string sceneName)
        {
            Dispatcher.Invoke(() =>
            {
                TxtCurrentScene.Text = sceneName;

                // Aggiorna selezione nella listbox senza retriggherare
                SceneListBox.SelectionChanged -= SceneListBox_SelectionChanged;
                foreach (var item in SceneListBox.Items)
                {
                    if (item is OBSWebsocketDotNet.Types.SceneBasicInfo info
                        && info.Name == sceneName)
                    {
                        SceneListBox.SelectedItem = item;
                        break;
                    }
                }
                SceneListBox.SelectionChanged += SceneListBox_SelectionChanged;
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  ANTEPRIMA
        // ═══════════════════════════════════════════════════════════════════

        private void PreviewTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isConnected) return;

            try
            {
                // Screenshot asincrono per non bloccare la UI
                string? dataUri = _obs.GetScreenshot(null, 1280, 720);
                if (dataUri is null) return;

                // Converti data-URI → BitmapImage
                string base64 = dataUri.Contains(",")
                    ? dataUri[(dataUri.IndexOf(',') + 1)..]
                    : dataUri;

                byte[] imageBytes = Convert.FromBase64String(base64);
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(imageBytes);
                bmp.BeginInit();
                bmp.StreamSource   = ms;
                bmp.CacheOption    = BitmapCacheOption.OnLoad;
                bmp.CreateOptions  = BitmapCreateOptions.None;
                bmp.EndInit();
                bmp.Freeze(); // Necessario per cross-thread

                Dispatcher.Invoke(() =>
                {
                    PreviewImage.Source           = bmp;
                    ErrorOverlay.Visibility       = Visibility.Collapsed;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    _frameCount++;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtError.Text            = $"Anteprima non disponibile:\n{ex.Message}";
                    ErrorOverlay.Visibility  = Visibility.Visible;
                });
            }
        }

        private void CmbRefreshRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRefreshRate.SelectedItem is ComboBoxItem item
                && item.Tag is string tagStr
                && int.TryParse(tagStr, out int ms))
            {
                _previewIntervalMs = ms;
                if (_previewTimer.IsEnabled)
                {
                    _previewTimer.Stop();
                    _previewTimer.Interval = TimeSpan.FromMilliseconds(ms);
                    _previewTimer.Start();
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  VIRTUAL CAMERA
        // ═══════════════════════════════════════════════════════════════════

        private void RefreshVCamStatus()
        {
            try
            {
                bool active = _obs.GetVirtualCamStatus();
                SetVCamUI(active);
            }
            catch
            {
                SetVCamUI(false);
            }
        }

        private void BtnVirtualCam_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected) return;

            try
            {
                bool nowActive = _obs.ToggleVirtualCam();
                SetVCamUI(nowActive);
                SetStatus(nowActive
                    ? "Virtual Camera avviata"
                    : "Virtual Camera fermata");
            }
            catch (Exception ex)
            {
                SetStatus($"⚠  Virtual Camera: {ex.Message}");
            }
        }

        private void SetVCamUI(bool active)
        {
            _isVCamActive = active;

            if (active)
            {
                VCamDot.Fill        = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                VCamDot.Effect      = new System.Windows.Media.Effects.DropShadowEffect
                    { Color = Color.FromRgb(0xE5, 0x39, 0x35), BlurRadius = 8, ShadowDepth = 0 };
                TxtVCamStatus.Text      = "Attiva";
                TxtVCamStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                BtnVirtualCam.Content   = "⏹  Ferma Virtual Camera";
            }
            else
            {
                VCamDot.Fill         = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                VCamDot.Effect       = null;
                TxtVCamStatus.Text       = "Inattiva";
                TxtVCamStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                BtnVirtualCam.Content    = "▶  Avvia Virtual Camera";
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  INVIA IMMAGINE
        // ═══════════════════════════════════════════════════════════════════

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Seleziona immagine da inviare a OBS",
                Filter = "Immagini (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Tutti i file (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                _selectedImagePath = dlg.FileName;
                TxtImagePath.Text  = Path.GetFileName(_selectedImagePath);
                TxtImagePath.ToolTip = _selectedImagePath;
                TxtImagePath.Foreground = new SolidColorBrush(Colors.White);

                // Thumbnail preview
                try
                {
                    var bmp = new BitmapImage(new Uri(_selectedImagePath));
                    ThumbImage.Source        = bmp;
                    ThumbBorder.Visibility   = Visibility.Visible;
                }
                catch
                {
                    ThumbBorder.Visibility = Visibility.Collapsed;
                }

                BtnSendImage.IsEnabled = _isConnected;
                SetStatus($"Immagine selezionata: {Path.GetFileName(_selectedImagePath)}");
            }
        }

        private void BtnSendImage_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || string.IsNullOrEmpty(_selectedImagePath)) return;

            string sourceName = TxtSourceName.Text.Trim();
            if (string.IsNullOrEmpty(sourceName))
            {
                SetStatus("⚠  Inserisci il nome della sorgente OBS");
                return;
            }

            if (!File.Exists(_selectedImagePath))
            {
                SetStatus("⚠  File immagine non trovato");
                return;
            }

            try
            {
                // Verifica esistenza sorgente e avvisa se non esiste
                if (!_obs.InputExists(sourceName))
                {
                    SetStatus($"⚠  Sorgente \"{sourceName}\" non trovata in OBS — creala manualmente come 'image_source'");
                    return;
                }

                _obs.SetImageSourcePath(sourceName, _selectedImagePath);
                SetStatus($"✓  Immagine inviata → {sourceName}");
            }
            catch (Exception ex)
            {
                SetStatus($"⚠  Invio immagine fallito: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CONFIGURATION
        // ═══════════════════════════════════════════════════════════════════

        private void LoadConfigurationDefaults()
        {
            try
            {
                // Prova a leggere appsettings.json
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JObject.Parse(json);

                    string ip = config["OBS"]?["IP"]?.ToString() ?? "localhost";
                    int port = config["OBS"]?["Port"]?.Value<int>() ?? 4455;
                    string pwd = config["OBS"]?["Password"]?.ToString() ?? "";

                    TxtIP.Text = ip;
                    TxtPort.Text = port.ToString();
                    TxtPassword.Password = pwd;
                }
            }
            catch
            {
                // Se fallisce, usa i default
                TxtIP.Text = "localhost";
                TxtPort.Text = "4455";
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UTILITY
        // ═══════════════════════════════════════════════════════════════════

        private void SetStatus(string msg)
        {
            Dispatcher.Invoke(() => StatusBar.Text = msg);
        }

        protected override void OnClosed(EventArgs e)
        {
            _previewTimer.Stop();
            _obs.Disconnect();
            base.OnClosed(e);
        }
    }
}
