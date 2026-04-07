using System.Windows;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OBSController
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                string configPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JObject.Parse(json);

                    // OBS Settings
                    TxtOBSIP.Text = config["OBS"]?["IP"]?.ToString() ?? "localhost";
                    TxtOBSPort.Text = config["OBS"]?["Port"]?.ToString() ?? "4455";
                    TxtOBSPassword.Password = config["OBS"]?["Password"]?.ToString() ?? "";

                    // Camera Settings
                    TxtCameraIP.Text = config["Camera"]?["IP"]?.ToString() ?? "192.168.1.50";
                    TxtCameraRTSP.Text = config["Camera"]?["RTSPStream"]?.ToString() ?? "rtsp://192.168.1.50/free";
                    TxtGo2RTCEndpoint.Text = config["Camera"]?["Go2RTCEndpoint"]?.ToString() ?? "http://localhost:1984";

                    // Application Settings
                    ChkAutoConnect.IsChecked = config["Application"]?["AutoConnect"]?.Value<bool>() ?? true;
                    ChkVirtualCamAuto.IsChecked = config["Application"]?["AutoStartVirtualCam"]?.Value<bool>() ?? false;
                }
            }
            catch
            {
                // Default values already set in XAML
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = new JObject
                {
                    ["OBS"] = new JObject
                    {
                        ["IP"] = TxtOBSIP.Text,
                        ["Port"] = int.Parse(TxtOBSPort.Text),
                        ["Password"] = TxtOBSPassword.Password
                    },
                    ["Camera"] = new JObject
                    {
                        ["IP"] = TxtCameraIP.Text,
                        ["RTSPStream"] = TxtCameraRTSP.Text,
                        ["Go2RTCEndpoint"] = TxtGo2RTCEndpoint.Text
                    },
                    ["Application"] = new JObject
                    {
                        ["AutoConnect"] = ChkAutoConnect.IsChecked ?? true,
                        ["AutoStartVirtualCam"] = ChkVirtualCamAuto.IsChecked ?? false,
                        ["PreviewRefreshMs"] = 1000,
                        ["ScreenshotWidth"] = 400,
                        ["ScreenshotHeight"] = 400,
                        ["ScreenshotQuality"] = 70
                    }
                };

                string configPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                File.WriteAllText(configPath, config.ToString(Newtonsoft.Json.Formatting.Indented));

                MessageBox.Show("Impostazioni salvate con successo!", "Successo", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore nel salvataggio: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
