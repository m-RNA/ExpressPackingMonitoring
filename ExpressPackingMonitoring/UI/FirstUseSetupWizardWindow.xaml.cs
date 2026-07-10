#nullable disable
using ExpressPackingMonitoring.Config;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.UI;

public partial class FirstUseSetupWizardWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<TextBlock> _stepTexts;
    private int _stepIndex;
    private bool _isLoadingDevices;
    private VideoCaptureDevice _previewCamera;
    private Task _previewCameraForceStopTask;
    private DateTime _lastPreviewUpdateAt = DateTime.MinValue;
    private WasapiCapture _micCapture;
    private readonly string _testBarcodeValue = $"TEST{DateTime.Now:yyyyMMddHHmmss}";
    private bool _scannerDetectedEnter;

    public bool WasSkipped { get; private set; }
    public AppConfig ResultConfig => _config;

    public FirstUseSetupWizardWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _stepTexts = new List<TextBlock> { StepModeText, StepCameraText, StepMicText, StepScannerText, StepDoneText };

        ContinuousModeRadio.IsChecked = !_config.EnableSameBarcodeStopRecording;
        SameCodeModeRadio.IsChecked = _config.EnableSameBarcodeStopRecording;
        RenderTestBarcode();

        Loaded += FirstUseSetupWizardWindow_Loaded;
        Closed += FirstUseSetupWizardWindow_Closed;
        ShowStep(0);
    }

    private void RenderTestBarcode()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = 430,
                Height = 120,
                Margin = 12,
                PureBarcode = false
            }
        };

        var pixelData = writer.Write(_testBarcodeValue);
        var source = BitmapSource.Create(
            pixelData.Width,
            pixelData.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixelData.Pixels,
            pixelData.Width * 4);
        source.Freeze();

        TestBarcodeImage.Source = source;
        TestBarcodeText.Text = $"屏幕测试条码：{_testBarcodeValue}，也可以扫描任意真实面单条码。";
    }

    private async void FirstUseSetupWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingDevices = true;
        try
        {
            await LoadDevicesAsync();
        }
        finally
        {
            _isLoadingDevices = false;
        }
    }

    private async Task LoadDevicesAsync()
    {
        var result = await RunOnStaThread(() =>
        {
            var cameras = new List<CameraInfo>();
            var mics = new List<MicInfo>();

            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                for (int i = 0; i < videoDevices.Count; i++)
                {
                    cameras.Add(new CameraInfo
                    {
                        Index = i,
                        Name = $"[{i}] {videoDevices[i].Name}",
                        Moniker = videoDevices[i].MonikerString
                    });
                }
            }
            catch { }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var audioDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in audioDevices)
                {
                    mics.Add(new MicInfo { Name = device.FriendlyName, Moniker = device.ID });
                }
            }
            catch { }

            return (Cameras: cameras, Mics: mics);
        });

        var cameras = result.Cameras;
        if (cameras.Count == 0)
        {
            cameras.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头", Moniker = "" });
        }

        CameraComboBox.ItemsSource = cameras;
        CameraComboBox.SelectedItem = cameras.FirstOrDefault(c => !string.IsNullOrEmpty(_config.CameraMonikerString) && c.Moniker == _config.CameraMonikerString)
            ?? cameras.FirstOrDefault(c => c.Index == _config.CameraIndex)
            ?? cameras.FirstOrDefault();

        var mics = result.Mics;
        if (mics.Count == 0)
        {
            mics.Add(new MicInfo { Name = "未检测到麦克风", Moniker = "" });
        }

        MicComboBox.ItemsSource = mics;
        MicComboBox.SelectedItem = mics.FirstOrDefault(m => !string.IsNullOrEmpty(_config.AudioDeviceMoniker) && m.Moniker == _config.AudioDeviceMoniker)
            ?? mics.FirstOrDefault(m => m.Name == _config.AudioDeviceName)
            ?? mics.FirstOrDefault(IsAvailableMic)
            ?? mics.FirstOrDefault();
    }

    private static Task<T> RunOnStaThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private void ShowStep(int stepIndex)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, 4);
        ModePage.Visibility = _stepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraPage.Visibility = _stepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        MicPage.Visibility = _stepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ScannerPage.Visibility = _stepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        DonePage.Visibility = _stepIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _stepTexts.Count; i++)
        {
            _stepTexts[i].Foreground = i == _stepIndex
                ? (System.Windows.Media.Brush)FindResource("AccentBlue")
                : (System.Windows.Media.Brush)FindResource("TextSecondary");
            _stepTexts[i].FontWeight = i == _stepIndex ? FontWeights.Black : FontWeights.SemiBold;
        }

        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = _stepIndex == 4 ? "完成" : "下一步";

        if (_stepIndex == 1)
        {
            StartCameraPreviewFromSelection();
        }
        else
        {
            StopCameraPreview();
        }

        if (_stepIndex == 2)
        {
            StartMicPreviewFromSelection();
        }
        else
        {
            StopMicPreview();
        }

        if (_stepIndex == 3)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ScanTestTextBox.Focus();
                ScanTestTextBox.SelectAll();
            }));
        }

        if (_stepIndex == 4)
        {
            UpdateFlowText();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == 1 && !TryLeaveCameraStep())
            return;

        if (_stepIndex == 4)
        {
            ApplySelections();
            WasSkipped = false;
            DialogResult = true;
            Close();
            return;
        }

        ShowStep(_stepIndex + 1);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == 1 && !TryLeaveCameraStep())
            return;
        ShowStep(_stepIndex - 1);
    }

    private bool TryLeaveCameraStep()
    {
        if (StopCameraPreview())
            return true;

        CameraStatusText.Text = "摄像头未能停止，请重新插拔后再继续";
        CameraStatusText.Visibility = Visibility.Visible;
        return false;
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        WasSkipped = true;
        DialogResult = true;
        Close();
    }

    private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDevices) return;
        if (_stepIndex == 1)
        {
            StartCameraPreviewFromSelection();
        }
    }

    private void StartCameraPreviewFromSelection()
    {
        if (!StopCameraPreview())
        {
            CameraStatusText.Text = "上一个摄像头未能停止，请重新插拔后重试";
            CameraStatusText.Visibility = Visibility.Visible;
            return;
        }
        CameraPreviewImage.Source = null;

        if (CameraComboBox.SelectedItem is not CameraInfo camera || string.IsNullOrEmpty(camera.Moniker))
        {
            CameraStatusText.Text = "未检测到可用摄像头";
            CameraStatusText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _previewCamera = new VideoCaptureDevice(camera.Moniker);
            if (_previewCamera.VideoCapabilities.Length > 0)
            {
                _previewCamera.VideoResolution = SelectBestCapability(_previewCamera.VideoCapabilities);
            }

            _previewCamera.NewFrame += PreviewCamera_NewFrame;
            _previewCamera.Start();
            CameraStatusText.Text = "正在等待摄像头画面...";
            CameraStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = $"摄像头预览启动失败：{ex.Message}";
            CameraStatusText.Visibility = Visibility.Visible;
            StopCameraPreview();
        }
    }

    private VideoCapabilities SelectBestCapability(VideoCapabilities[] capabilities)
    {
        var best = capabilities[0];
        int bestScore = int.MaxValue;
        foreach (var capability in capabilities)
        {
            int resDiff = Math.Abs(capability.FrameSize.Width - _config.FrameWidth) + Math.Abs(capability.FrameSize.Height - _config.FrameHeight);
            int fpsDiff = Math.Abs(capability.AverageFrameRate - _config.Fps);
            int score = resDiff * 10 + fpsDiff;
            if (score < bestScore)
            {
                bestScore = score;
                best = capability;
            }
        }

        return best;
    }

    private void PreviewCamera_NewFrame(object sender, NewFrameEventArgs eventArgs)
    {
        if (DateTime.UtcNow - _lastPreviewUpdateAt < TimeSpan.FromMilliseconds(100)) return;
        _lastPreviewUpdateAt = DateTime.UtcNow;

        try
        {
            using var bitmap = (Bitmap)eventArgs.Frame.Clone();
            BitmapSource source = ConvertBitmapToSource(bitmap);
            source.Freeze();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CameraPreviewImage.Source = source;
                CameraStatusText.Visibility = Visibility.Collapsed;
            }));
        }
        catch { }
    }

    private static BitmapSource ConvertBitmapToSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;
        var source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return source;
    }

    private bool StopCameraPreview()
    {
        VideoCaptureDevice camera = _previewCamera;
        if (camera == null) return true;

        try { camera.NewFrame -= PreviewCamera_NewFrame; } catch { }
        try
        {
            if (camera.IsRunning)
            {
                camera.SignalToStop();
                for (int i = 0; i < 20 && camera.IsRunning; i++)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch { }

        if (camera.IsRunning)
        {
            if (_previewCameraForceStopTask == null || _previewCameraForceStopTask.IsCompleted)
                _previewCameraForceStopTask = Task.Run(() => camera.Stop());
            try { _previewCameraForceStopTask.Wait(2000); } catch { }
        }

        if (camera.IsRunning)
            return false;

        if (ReferenceEquals(_previewCamera, camera))
            _previewCamera = null;
        _previewCameraForceStopTask = null;
        return true;
    }

    private void MicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDevices) return;
        if (_stepIndex == 2)
        {
            StartMicPreviewFromSelection();
        }
    }

    private void StartMicPreviewFromSelection()
    {
        StopMicPreview();
        MicLevelBar.Value = 0;

        if (MicComboBox.SelectedItem is not MicInfo mic || !IsAvailableMic(mic))
        {
            MicStatusText.Text = "未检测到可用麦克风";
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = !string.IsNullOrEmpty(mic.Moniker)
                ? enumerator.GetDevice(mic.Moniker)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _micCapture = new WasapiCapture(device);
            _micCapture.DataAvailable += MicCapture_DataAvailable;
            _micCapture.RecordingStopped += (_, __) => Dispatcher.BeginInvoke(new Action(() => MicLevelBar.Value = 0));
            _micCapture.StartRecording();
            MicStatusText.Text = "请对着麦克风说话，观察音量条";
        }
        catch (Exception ex)
        {
            MicStatusText.Text = $"麦克风启动失败：{ex.Message}";
            StopMicPreview();
        }
    }

    private void MicCapture_DataAvailable(object sender, WaveInEventArgs e)
    {
        double peak = CalculatePeak(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);
        double value = Math.Clamp(peak * 140.0, 0, 100);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MicLevelBar.Value = value;
            if (value > 8)
            {
                MicStatusText.Text = "已检测到麦克风音量";
                MicStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
            }
        }));
    }

    private static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        double peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytesRecorded; i += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0));
            }
        }
        else if (format.BitsPerSample == 24)
        {
            for (int i = 0; i + 3 <= bytesRecorded; i += 3)
            {
                int sample = (buffer[i + 2] << 24) | (buffer[i + 1] << 16) | (buffer[i] << 8);
                peak = Math.Max(peak, Math.Abs(sample / 2147483648.0));
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(buffer, i) / 2147483648.0));
            }
        }

        return peak;
    }

    private void StopMicPreview()
    {
        if (_micCapture == null) return;

        try { _micCapture.DataAvailable -= MicCapture_DataAvailable; } catch { }
        try { _micCapture.StopRecording(); } catch { }
        _micCapture.Dispose();
        _micCapture = null;
    }

    private void ScanTestTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_stepIndex != 3) return;
        string content = ScanTestTextBox.Text.Trim();
        if (string.IsNullOrEmpty(content))
        {
            _scannerDetectedEnter = false;
            ScanStatusText.Text = "等待扫码内容...";
            ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
            return;
        }

        ScanStatusText.Text =
            "未检测到扫码枪自动回车，已准备切换为窗口内识别。\n" +
            "这种模式需要软件窗口在前台，避免影响其他输入。\n" +
            "建议按扫码枪说明书，或联系卖家开启“扫描后自动回车 / Enter 后缀”，体验会更稳定。";
        ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentOrange");
    }

    private void ScanTestTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            _scannerDetectedEnter = true;
            ScanStatusText.Text = "已检测到自动回车，可支持后台扫码。";
            ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
            e.Handled = true;
        }
    }

    private void UpdateFlowText()
    {
        if (SameCodeModeRadio.IsChecked == true)
        {
            FlowText.Text =
                "1. 扫描面单条码开始录制\n" +
                "2. 打包完成后再次扫描同一单号\n" +
                "3. 软件停止录制并保存视频";
        }
        else
        {
            FlowText.Text =
                "1. 放好快递\n" +
                "2. 扫描面单条码开始录制\n" +
                "3. 打包完成后扫描下一单\n" +
                "4. 软件自动保存上一单并开始下一单";
        }
    }

    private void ApplySelections()
    {
        _config.EnableSameBarcodeStopRecording = SameCodeModeRadio.IsChecked == true;
        _config.EnableAudioRecording = true;
        ApplyScannerModeFromTest();

        if (CameraComboBox.SelectedItem is CameraInfo camera && !string.IsNullOrEmpty(camera.Moniker))
        {
            _config.CameraIndex = camera.Index;
            _config.CameraMonikerString = camera.Moniker;
        }

        if (MicComboBox.SelectedItem is MicInfo mic && IsAvailableMic(mic))
        {
            _config.AudioDeviceName = mic.Name;
            _config.AudioDeviceMoniker = mic.Moniker ?? "";
        }

        if (!string.IsNullOrEmpty(_config.CameraMonikerString))
        {
            _config.CameraConfigs[_config.CameraMonikerString] = new CameraSettings
            {
                FrameWidth = _config.FrameWidth,
                FrameHeight = _config.FrameHeight,
                Fps = _config.Fps,
                AudioDeviceName = _config.AudioDeviceName,
                AudioDeviceMoniker = _config.AudioDeviceMoniker,
                AudioSyncOffsetMs = _config.AudioSyncOffsetMs
            };
        }
    }

    private void ApplyScannerModeFromTest()
    {
        string scannedText = ScanTestTextBox.Text?.Trim() ?? "";
        if (_scannerDetectedEnter)
        {
            _config.EnableGlobalKeyboard = true;
            _config.EnableScannerAutoSubmit = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(scannedText))
        {
            _config.EnableGlobalKeyboard = false;
            _config.EnableScannerAutoSubmit = true;
        }
    }

    private static bool IsAvailableMic(MicInfo mic)
    {
        return mic != null
            && !string.IsNullOrWhiteSpace(mic.Name)
            && mic.Name != "未检测到麦克风";
    }

    private void FirstUseSetupWizardWindow_Closed(object sender, EventArgs e)
    {
        StopCameraPreview();
        StopMicPreview();
    }
}
