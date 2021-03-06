﻿using AudioVisualizer;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using TK_Recorder.Capture;
using TK_Recorder.Model;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.ExtendedExecution.Foreground;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.ApplicationModel;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace TK_Recorder
{
    public sealed partial class MainPage : Page
    {
        private IDirect3DDevice screenDevice;
        private Encoder screenCapture;
        private GraphicsCaptureItem captureItem = null;

        private MediaCapture mediaCapture;
        private LowLagMediaRecording mediaRecording;

        private AudioGraph audioGraph;

        private StorageFolder storageFolder = null;

        private DispatcherTimer _timer = new DispatcherTimer();
        private long _timerCount = 0;

        public MainPage()
        {
            InitializeComponent();

            if (!GraphicsCaptureSession.IsSupported())
            {
                IsEnabled = false;

                var dialog = new MessageDialog(
                    "Screen capture is not supported on this device for this release of Windows!",
                    "Screen capture unsupported");

                var ignored = dialog.ShowAsync();
                return;
            }

            // initialize screen recording
            screenDevice = Direct3D11Helpers.CreateDevice();

            // connect to the powerpoint app service
            App.AppServiceConnected += MainPage_AppServiceConnected;

            _timer.Interval = new TimeSpan(0, 0, 1);
            _timer.Tick += _timer_Tick;
        }

        private void _timer_Tick(object sender, object e)
        {
            _timerCount += 1;

            TimeSpan time = TimeSpan.FromSeconds(_timerCount);
            TimerCounter.Text = time.ToString(@"hh\:mm\:ss");
        }

        #region Device initialization
        private void PopulateVideoDeviceProperties(MediaStreamType streamType, ComboBox comboBox, bool showFrameRate = true)
        {
            // query all properties of the specified video stream type 
            IEnumerable<StreamPropertiesHelper> allStreamProperties =
                mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(streamType)
                .Select(x => new StreamPropertiesHelper(x));

            // order them by resolution then frame rate
            allStreamProperties = allStreamProperties
                .OrderByDescending(x => x.Height * x.Width)
                .ThenByDescending(x => x.FrameRate);

            // populate the combo box with the entries
            foreach (var property in allStreamProperties)
            {
                var comboBoxItem = new ComboBoxItem();
                comboBoxItem.Content = property.GetFriendlyName(showFrameRate);
                comboBoxItem.Tag = property;
                comboBox.Items.Add(comboBoxItem);
            }

            var settings = AppSettingsContainer.GetCachedSettings();
            comboBox.SelectedItem = WebcamComboBox.Items.Where(x => (x as ComboBoxItem).Content.ToString() == settings.WebcamQuality).FirstOrDefault();
        }

        private async Task InitAudioMeterAsync()
        {
            var result = await AudioGraph.CreateAsync(new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Speech));
            if (result.Status == AudioGraphCreationStatus.Success)
            {
                this.audioGraph = result.Graph;

                var audioDevice = (AudioDeviceComboBox.SelectedItem as ComboBoxItem);
                if (audioDevice == null)
                    return;

                var microphone = await DeviceInformation.CreateFromIdAsync(audioDevice.Tag.ToString());
                var inProfile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                var inputResult = await this.audioGraph.CreateDeviceInputNodeAsync(MediaCategory.Speech, inProfile.Audio, microphone);

                if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    var msg = new MessageDialog("Device is not available");
                    await msg.ShowAsync();
                    return;
                }

                this.audioGraph.Start();

                var source = PlaybackSource.CreateFromAudioNode(inputResult.DeviceInputNode);
                AudioDiscreteVUBar.Source = source.Source;
            }
        }

        private async Task PopulateAudioAndVideoDevicesAsync()
        {
            // Finds all video capture devices
            var videoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            foreach (var device in videoDevices)
            {
                var comboBoxItem = new ComboBoxItem();
                comboBoxItem.Content = device.Name;
                comboBoxItem.Tag = device.Id;
                WebcamDeviceComboBox.Items.Add(comboBoxItem);
            }

            // find all audio devices
            var audioDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);

            foreach (var device in audioDevices)
            {
                var comboBoxItem = new ComboBoxItem();
                comboBoxItem.Content = device.Name;
                comboBoxItem.Tag = device.Id;
                AudioDeviceComboBox.Items.Add(comboBoxItem);
            }
        }

        private async Task InitWebcamAsync(string videoDeviceId, string audioDeviceId)
        {
            if (mediaCapture != null)
            {
                await mediaCapture.StopPreviewAsync();
            }

            // create new media capture
            mediaCapture = new MediaCapture();
            mediaCapture.RecordLimitationExceeded += CaptureManager_RecordLimitationExceeded;

            await mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings()
            {
                VideoDeviceId = videoDeviceId,
                AudioDeviceId = audioDeviceId
            });

            WebcamPreview.Source = mediaCapture;
            await mediaCapture.StartPreviewAsync();
        }
        #endregion

        #region Settings
        private async Task LoadSettings()
        {
            var settings = AppSettingsContainer.GetCachedSettings();

            // load quality settings
            var names = new List<string>
            {
                nameof(VideoEncodingQuality.HD1080p),
                nameof(VideoEncodingQuality.HD720p),
                nameof(VideoEncodingQuality.Uhd2160p)
            };
            QualityComboBox.ItemsSource = names;
            QualityComboBox.SelectedIndex = names.IndexOf(settings.Quality.ToString());

            var frameRates = new List<string> { "15fps", "30fps", "60fps" };
            FrameRateComboBox.ItemsSource = frameRates;
            FrameRateComboBox.SelectedIndex = frameRates.IndexOf($"{settings.FrameRate}fps");

            UseCaptureItemSizeCheckBox.IsChecked = settings.UseSourceSize;
            AdaptBitrateCheckBox.IsChecked = settings.AdaptBitrate;

            // load default storage path
            if (!string.IsNullOrEmpty(settings.StorageFolder))
            {
                try
                {
                    storageFolder = await StorageFolder.GetFolderFromPathAsync(settings.StorageFolder);
                    FolderName.Text = settings.StorageFolder;
                }
                catch
                {
                    var dialog = new MessageDialog(
                    "Previous storage directory does not exist anymore. Please select a new directory.",
                    "Directory not found or not enough permissions");

                    await dialog.ShowAsync();
                }
            }

            // set first webcam device
            WebcamDeviceComboBox.SelectedItem = WebcamDeviceComboBox.Items.Where(x => (x as ComboBoxItem).Tag.ToString() == settings.WebcamDeviceId).FirstOrDefault();
            AudioDeviceComboBox.SelectedItem = AudioDeviceComboBox.Items.Where(x => (x as ComboBoxItem).Tag.ToString() == settings.AudioDeviceId).FirstOrDefault();
        }

        private AppSettings GetCurrentSettings()
        {
            try
            {
                var quality = AppSettingsContainer.ParseEnumValue<VideoEncodingQuality>((string)QualityComboBox.SelectedItem);
                var frameRate = uint.Parse(((string)FrameRateComboBox.SelectedItem).Replace("fps", ""));
                var useSourceSize = UseCaptureItemSizeCheckBox.IsChecked.Value;
                var adaptBitrate = AdaptBitrateCheckBox.IsChecked.Value;
                var webcamQuality = (WebcamComboBox.SelectedItem as ComboBoxItem).Content.ToString();

                return new AppSettings
                {
                    Quality = quality,
                    FrameRate = frameRate,
                    UseSourceSize = useSourceSize,
                    AudioDeviceId = (AudioDeviceComboBox.SelectedItem as ComboBoxItem).Tag.ToString(),
                    WebcamDeviceId = (WebcamDeviceComboBox.SelectedItem as ComboBoxItem).Tag.ToString(),
                    WebcamQuality = webcamQuality,
                    AdaptBitrate = adaptBitrate,
                    StorageFolder = storageFolder.Path,
                    WebcamExposure = (long)ExposureSlider.Value,
                    WebcamWhiteBalance = (uint)WbSlider.Value,
                    WebcamExposureAuto = ExposureAutoCheckBox.IsChecked.HasValue ? ExposureAutoCheckBox.IsChecked.Value : true,
                    WebcamWhiteBalanceAuto = WbAutoCheckBox.IsChecked.HasValue ? WbAutoCheckBox.IsChecked.Value : true
                };
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void CacheCurrentSettings()
        {
            var settings = GetCurrentSettings();
            AppSettingsContainer.CacheSettings(settings);
        }
        #endregion

        #region Webcam settings

        private void SetExposureControls()
        {
            // exposure control
            var exposureControl = mediaCapture.VideoDeviceController.ExposureControl;

            if (exposureControl.Supported)
            {
                ExposureAutoCheckBox.Visibility = Visibility.Visible;
                ExposureSlider.Visibility = Visibility.Visible;

                ExposureAutoCheckBox.IsChecked = exposureControl.Auto;

                ExposureSlider.Minimum = exposureControl.Min.Ticks;
                ExposureSlider.Maximum = exposureControl.Max.Ticks;
                ExposureSlider.StepFrequency = exposureControl.Step.Ticks;

                ExposureSlider.ValueChanged -= ExposureSlider_ValueChanged;
                var value = exposureControl.Value;
                ExposureSlider.Value = value.Ticks;
                ExposureSlider.ValueChanged += ExposureSlider_ValueChanged;
            }
            else
            {
                var exposure = mediaCapture.VideoDeviceController.Exposure;
                double value;

                if (exposure.TryGetValue(out value))
                {
                    ExposureSlider.Minimum = exposure.Capabilities.Min;
                    ExposureSlider.Maximum = exposure.Capabilities.Max;
                    ExposureSlider.StepFrequency = exposure.Capabilities.Step;

                    ExposureSlider.ValueChanged -= ExposureSlider_ValueChanged;
                    ExposureSlider.Value = value;
                    ExposureSlider.ValueChanged += ExposureSlider_ValueChanged;
                }
                else
                {
                    ExposureSlider.Visibility = Visibility.Collapsed;
                }

                bool autoValue;
                if (exposure.TryGetAuto(out autoValue))
                {
                    ExposureAutoCheckBox.IsChecked = autoValue;
                }
                else
                {
                    ExposureAutoCheckBox.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void SetWhiteBalanceControl()
        {
            // white balance control
            var whiteBalanceControl = mediaCapture.VideoDeviceController.WhiteBalanceControl;

            if (whiteBalanceControl.Supported)
            {
                WbSlider.Visibility = Visibility.Visible;
                WbComboBox.Visibility = Visibility.Visible;

                if (WbComboBox.ItemsSource == null)
                {
                    WbComboBox.ItemsSource = Enum.GetValues(typeof(ColorTemperaturePreset)).Cast<ColorTemperaturePreset>();
                }

                WbComboBox.SelectedItem = whiteBalanceControl.Preset;

                if (whiteBalanceControl.Max - whiteBalanceControl.Min > whiteBalanceControl.Step)
                {
                    WbSlider.Minimum = whiteBalanceControl.Min;
                    WbSlider.Maximum = whiteBalanceControl.Max;
                    WbSlider.StepFrequency = whiteBalanceControl.Step;

                    WbSlider.ValueChanged -= WbSlider_ValueChanged;
                    WbSlider.Value = whiteBalanceControl.Value;
                    WbSlider.ValueChanged += WbSlider_ValueChanged;
                }
                else
                {
                    WbSlider.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                WbComboBox.Visibility = Visibility.Collapsed;

                var whitebalance = mediaCapture.VideoDeviceController.WhiteBalance;
                double value;

                if (whitebalance.TryGetValue(out value))
                {
                    WbSlider.Minimum = whitebalance.Capabilities.Min;
                    WbSlider.Maximum = whitebalance.Capabilities.Max;
                    WbSlider.StepFrequency = whitebalance.Capabilities.Step;

                    WbSlider.ValueChanged -= WbSlider_ValueChanged;
                    WbSlider.Value = value;
                    WbSlider.ValueChanged += WbSlider_ValueChanged;
                }
                else
                {
                    WbSlider.Visibility = Visibility.Collapsed;
                }

                bool autoValue;
                if (whitebalance.TryGetAuto(out autoValue))
                {
                    WbAutoCheckBox.Visibility = Visibility.Visible;
                    WbAutoCheckBox.IsChecked = autoValue;

                    WbAutoCheckBox.Checked += WbCheckBox_CheckedChanged;
                    WbAutoCheckBox.Unchecked += WbCheckBox_CheckedChanged;
                }
            }
        }

        private void WbCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (mediaCapture.VideoDeviceController.WhiteBalance.Capabilities.AutoModeSupported)
            {
                mediaCapture.VideoDeviceController.WhiteBalance.TrySetAuto(WbAutoCheckBox.IsChecked.Value);

                if (!WbAutoCheckBox.IsChecked.Value)
                {
                    mediaCapture.VideoDeviceController.WhiteBalance.TrySetValue(WbSlider.Value);
                }
            }
        }

        private async void WebcamComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selectedItem = (sender as ComboBox).SelectedItem as ComboBoxItem;
                if (selectedItem == null)
                    return;

                var encodingProperties = (selectedItem.Tag as StreamPropertiesHelper).EncodingProperties;
                await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, encodingProperties);

                SetExposureControls();
                SetWhiteBalanceControl();

                // load settings
                var settings = AppSettingsContainer.GetCachedSettings();

                if (ExposureAutoCheckBox.Visibility == Visibility.Visible && ExposureSlider.Visibility == Visibility.Visible)
                {
                    ExposureSlider.Value = settings.WebcamExposure;
                    ExposureAutoCheckBox.IsChecked = settings.WebcamExposureAuto;
                }

                if (WbSlider.Visibility == Visibility.Visible)
                {
                    WbSlider.Value = settings.WebcamWhiteBalance;
                }

                if (WbAutoCheckBox.Visibility == Visibility.Visible)
                {
                    WbAutoCheckBox.IsChecked = settings.WebcamWhiteBalanceAuto;
                }
            }
            catch (Exception ex)
            {
                var msg = new MessageDialog("The device is not ready.");
                await msg.ShowAsync();
            }
        }

        private async void WebcamDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var webcamDevice = WebcamDeviceComboBox.SelectedItem as ComboBoxItem;
            var audioDevice = AudioDeviceComboBox.SelectedItem as ComboBoxItem;

            if (webcamDevice == null || audioDevice == null)
                return;

            try
            {
                await InitWebcamAsync(webcamDevice.Tag.ToString(), audioDevice.Tag.ToString());
                PopulateVideoDeviceProperties(MediaStreamType.VideoRecord, WebcamComboBox, true);
            }
            catch (Exception ex)
            {
                var msg = new MessageDialog("Unauthorized access to video and audio recording. Please change setting in Windows settings.");
                await msg.ShowAsync();
            }
        }

        private async void AudioDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var webcamDevice = WebcamDeviceComboBox.SelectedItem as ComboBoxItem;
            var audioDevice = AudioDeviceComboBox.SelectedItem as ComboBoxItem;

            if (webcamDevice == null || audioDevice == null)
                return;

            try
            {
                await InitWebcamAsync(webcamDevice.Tag.ToString(), audioDevice.Tag.ToString());
                PopulateVideoDeviceProperties(MediaStreamType.VideoRecord, WebcamComboBox, true);

                await InitAudioMeterAsync();
            }
            catch (UnauthorizedAccessException ex)
            {
                var msg = new MessageDialog("Unauthorized access to video and audio recording. Please change setting in Windows settings.");
                await msg.ShowAsync();
            }
        }

        private async void ExposureSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            var value = TimeSpan.FromTicks((long)(sender as Slider).Value);

            if (mediaCapture.VideoDeviceController.ExposureControl.Supported)
            {
                await mediaCapture.VideoDeviceController.ExposureControl.SetValueAsync(value);
            }
            else
            {
                mediaCapture.VideoDeviceController.Exposure.TrySetValue((long)(sender as Slider).Value);
            }
        }

        private async void ExposureCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            var autoExposure = ((sender as CheckBox).IsChecked == true);

            if (mediaCapture.VideoDeviceController.ExposureControl.Supported)
            {
                await mediaCapture.VideoDeviceController.ExposureControl.SetAutoAsync(autoExposure);
            }
            else
            {
                mediaCapture.VideoDeviceController.Exposure.TrySetAuto(autoExposure);

                if (!autoExposure)
                {
                    mediaCapture.VideoDeviceController.Exposure.TrySetValue(ExposureSlider.Value);
                }
            }
        }

        private async void WbComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = (ColorTemperaturePreset)WbComboBox.SelectedItem;
            WbSlider.IsEnabled = (selected == ColorTemperaturePreset.Manual);

            await mediaCapture.VideoDeviceController.WhiteBalanceControl.SetPresetAsync(selected);
        }

        private async void WbSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            var value = (sender as Slider).Value;

            if (mediaCapture.VideoDeviceController.WhiteBalanceControl.Supported)
            {
                await mediaCapture.VideoDeviceController.WhiteBalanceControl.SetValueAsync((uint)value);
            }
            else
            {
                mediaCapture.VideoDeviceController.WhiteBalance.TrySetValue(value);
            }
        }
        #endregion

        #region Recording
        private async void ToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            var button = (ToggleButton)sender;

            // get storage folder
            if (storageFolder == null)
            {
                var msg = new MessageDialog("Please choose a folder first...");
                await msg.ShowAsync();

                button.IsChecked = false;
                return;
            }

            // check storage folder permissions
            try
            {
                var files = await storageFolder.GetFilesAsync();
            }
            catch
            {
                var dialog = new MessageDialog(
                    "The selected storage directory does not exist anymore or is not accessable. Please select a new directory.",
                    "Directory not found or not enough permissions");

                await dialog.ShowAsync();

                button.IsChecked = false;
                return;
            }

            var requestSuspensionExtension = new ExtendedExecutionForegroundSession();
            requestSuspensionExtension.Reason = ExtendedExecutionForegroundReason.Unspecified;
            var requestExtensionResult = await requestSuspensionExtension.RequestExtensionAsync();

            // get storage files
            var time = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss_");

            var screenFile = await storageFolder.CreateFileAsync(time + "slides.mp4");
            var webcamFile = await storageFolder.CreateFileAsync(time + "talkinghead.mp4");
            var jsonFile = await storageFolder.CreateFileAsync(time + "meta.json");

            // get encoder properties
            var frameRate = uint.Parse(((string)FrameRateComboBox.SelectedItem).Replace("fps", ""));
            var quality = (VideoEncodingQuality)Enum.Parse(typeof(VideoEncodingQuality), (string)QualityComboBox.SelectedItem, false);
            var useSourceSize = UseCaptureItemSizeCheckBox.IsChecked.Value;

            var temp = MediaEncodingProfile.CreateMp4(quality);
            uint bitrate = 2500000;
            var width = temp.Video.Width;
            var height = temp.Video.Height;

            // get capture item
            var picker = new GraphicsCapturePicker();
            captureItem = await picker.PickSingleItemAsync();
            if (captureItem == null)
            {
                button.IsChecked = false;
                return;
            }

            // use the capture item's size for the encoding if desired
            if (useSourceSize)
            {
                width = (uint)captureItem.Size.Width;
                height = (uint)captureItem.Size.Height;
            }

            // we have a screen resolution of more than 4K?
            if (width > 1920)
            {
                var v = width / 1920;
                width = 1920;
                height /= v;
            }

            // even if we're using the capture item's real size,
            // we still want to make sure the numbers are even.
            width = EnsureEven(width);
            height = EnsureEven(height);

            // tell the user we've started recording
            var originalBrush = MainTextBlock.Foreground;
            MainTextBlock.Foreground = new SolidColorBrush(Colors.Red);
            MainProgressBar.IsIndeterminate = true;

            button.IsEnabled = false;

            MainTextBlock.Text = "3";
            await Task.Delay(1000);

            MainTextBlock.Text = "2";
            await Task.Delay(1000);

            MainTextBlock.Text = "1";
            await Task.Delay(1000);

            button.IsEnabled = true;

            MainTextBlock.Text = "● rec";

            _timerCount = 0;
            _timer.Start();

            try
            {
                // start webcam recording
                MediaEncodingProfile webcamEncodingProfile = null;

                if (AdaptBitrateCheckBox.IsChecked.Value)
                {
                    var selectedItem = WebcamComboBox.SelectedItem as ComboBoxItem;
                    var encodingProperties = (selectedItem.Tag as StreamPropertiesHelper);

                    if (encodingProperties.Height > 720)
                    {
                        webcamEncodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
                        webcamEncodingProfile.Video.Bitrate = 8000000;
                    }
                    else if (encodingProperties.Height > 480)
                    {
                        webcamEncodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
                        webcamEncodingProfile.Video.Bitrate = 5000000;
                    }
                    else
                    {
                        webcamEncodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                    }
                }
                else
                {
                    webcamEncodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                }

                mediaRecording = await mediaCapture.PrepareLowLagRecordToStorageFileAsync(webcamEncodingProfile, webcamFile);

                // kick off the screen encoding parallel
                using (var stream = await screenFile.OpenAsync(FileAccessMode.ReadWrite))
                using (screenCapture = new Encoder(screenDevice, captureItem))
                {
                    // webcam recording
                    if (mediaCapture != null)
                    {
                        await mediaRecording.StartAsync();
                    }

                    // screen recording
                    await screenCapture.EncodeAsync(
                        stream,
                        width, height, bitrate,
                        frameRate);
                }

                MainTextBlock.Foreground = originalBrush;

                // user has finished recording, so stop webcam recording
                await mediaRecording.StopAsync();
                await mediaRecording.FinishAsync();
                _timer.Stop();
            }
            catch (Exception ex)
            {
                _timer.Stop();

                var dialog = new MessageDialog(
                    $"Uh-oh! Something went wrong!\n0x{ex.HResult:X8} - {ex.Message}",
                    "Recording failed");

                await dialog.ShowAsync();

                button.IsChecked = false;
                MainTextBlock.Text = "failure";
                MainTextBlock.Foreground = originalBrush;
                MainProgressBar.IsIndeterminate = false;

                captureItem = null;
                if (mediaRecording != null)
                {
                    await mediaRecording.StopAsync();
                    await mediaRecording.FinishAsync();
                }

                return;
            }

            // at this point the encoding has finished
            MainTextBlock.Text = "saving...";

            // save slide markers
            var recording = new Recording()
            {
                Slides = screenCapture.GetTimestamps()
            };

            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            var json = JsonConvert.SerializeObject(recording, Formatting.Indented, settings);
            await FileIO.WriteTextAsync(jsonFile, json);

            // add metadata
            var recordingMetadataDialog = new RecordingMetadataDialog();
            var recordingMetadataDialogResult = await recordingMetadataDialog.ShowAsync();

            if (recordingMetadataDialogResult == ContentDialogResult.Primary)
            {
                recording.Description = recordingMetadataDialog.LectureTitle;

                if (recordingMetadataDialog.LectureDate.HasValue)
                {
                    recording.LectureDate = recordingMetadataDialog.LectureDate.Value.DateTime;
                }
            }
            else
            {
                recording.Description = null;
                recording.LectureDate = DateTime.Now;
            }

            json = JsonConvert.SerializeObject(recording, Formatting.Indented, settings);
            await FileIO.WriteTextAsync(jsonFile, json);

            // tell the user we're done
            button.IsChecked = false;
            MainTextBlock.Text = "done";
            MainProgressBar.IsIndeterminate = false;

            requestSuspensionExtension.Dispose();
        }

        private void CaptureManager_RecordLimitationExceeded(MediaCapture sender)
        {
            // stop the recording
            screenCapture?.Dispose();

            MainTextBlock.Text = "Limit reached (3h)";
        }

        private void ToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            // If the encoder is doing stuff, tell it to stop
            screenCapture?.Dispose();
        }

        private uint EnsureEven(uint number)
        {
            return (number % 2 == 0) ? number : number + 1;
        }
        #endregion

        private void MainPage_AppServiceConnected(object sender, EventArgs e)
        {
            App.Connection.RequestReceived += AppService_RequestReceived;
        }

        private async void AppService_RequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var msg = args.Request.Message;
            var result = msg["TYPE"].ToString();

            if (result == "SlideChanged")
            {
                screenCapture?.AddCurrentTimestamp();
            }
            else if (result == "Status")
            {
                if (msg["STATUS"].ToString() == "CONNECTED")
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PowerPointGreen.Visibility = Visibility.Visible;
                        PowerPointRed.Visibility = Visibility.Collapsed;
                    });
                }
                else
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PowerPointGreen.Visibility = Visibility.Collapsed;
                        PowerPointRed.Visibility = Visibility.Visible;
                    });
                }
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // load external app and init webcam
            await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
            await PopulateAudioAndVideoDevicesAsync();

            await LoadSettings();

            //await InitAudioMeterAsync();
        }

        private async void BtnFolderPicker_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.ViewMode = PickerViewMode.List;
            folderPicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            folderPicker.FileTypeFilter.Add("*");

            storageFolder = await folderPicker.PickSingleFolderAsync();

            if (storageFolder != null)
            {
                FolderName.Text = storageFolder.Path;
            }
        }
    }
}
