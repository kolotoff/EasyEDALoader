using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using PCB;
using SCH;
using Forms = System.Windows.Forms;

namespace EasyEDA_Loader
{
    public partial class DialogWindow : Window
    {
        private EasyedaApi Api;
        private CancellationTokenSource cts;
        private CancellationTokenSource previewCts;
        private ObservableCollection<PartInfoViewModel> searchResults;
        private CanvasZoomPanHelper _footprintHelper;
        private CanvasZoomPanHelper _symbolHelper;
        private ComponentInfo _currentComponent;
        private EeFootprint3dModel _currentModel;
        private Root _currentRoot;
        private bool _isRestoringSession;
        private F3DPreviewHost _originalF3DPreview;
        private F3DPreviewHost _cleanF3DPreview;
        private const int GwlStyle = -16;
        private const int SwShow = 5;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonUp = 0x0208;
        private const int WmMouseWheel = 0x020A;
        private const int WmInput = 0x00FF;
        private const int RidInput = 0x10000003;
        private const int RidevRemove = 0x00000001;
        private const int RidevInputSink = 0x00000100;
        private const int RawInputMouse = 0;
        private const int HidUsagePageGeneric = 0x01;
        private const int HidUsageGenericMouse = 0x02;
        private const int RiMouseLeftButtonDown = 0x0001;
        private const int RiMouseLeftButtonUp = 0x0002;
        private const int RiMouseRightButtonDown = 0x0004;
        private const int RiMouseRightButtonUp = 0x0008;
        private const int RiMouseMiddleButtonDown = 0x0010;
        private const int RiMouseMiddleButtonUp = 0x0020;
        private const int RiMouseWheel = 0x0400;
        private const int VkLButton = 0x01;
        private const int VkRButton = 0x02;
        private const int VkMButton = 0x04;
        private const int MkLButton = 0x0001;
        private const int MkRButton = 0x0002;
        private const int MkMButton = 0x0010;
        private const long WsChild = 0x40000000L;
        private const long WsVisible = 0x10000000L;
        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsPopup = unchecked((long)0x80000000L);
        private const uint SwpShowWindow = 0x0040;
        private bool _f3dInputReady;
        private int _f3dMouseButtonState;
        private F3DPreviewHost _f3dInputSource;
        private HwndSource _f3dRawInputSource;
        private bool _f3dRawInputRegistered;
        private bool _isCriticalOperationActive;
        private bool _operationCompleted;
        private static readonly char[] PartNumberSeparators = { '\r', '\n', '\t', ' ', ',', ';', '|' };
        private static readonly string SessionStateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyEDA-Loader",
            "dialog-session.json");

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public int Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public int Type;
            public int Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawMouse
        {
            public ushort Flags;
            public ushort Reserved;
            public ushort ButtonFlags;
            public ushort ButtonData;
            public uint RawButtons;
            public int LastX;
            public int LastY;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInput
        {
            public RawInputHeader Header;
            public RawMouse Mouse;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            RawInputDevice[] devices,
            int numDevices,
            int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetRawInputData(
            IntPtr rawInput,
            int command,
            IntPtr data,
            ref int size,
            int headerSize);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public List<ComponentSelection> SelectedComponents { get; private set; }
        public bool RemoveWatermark => removeWatermarkCheckBox?.IsChecked == true;
        public bool CleanText => RemoveWatermark && cleanTextCheckBox?.IsChecked == true;
        public bool ImportLcscMechanicalLayers => importLcscMechanicalLayersCheckBox?.IsChecked == true;
        public Func<IReadOnlyList<ComponentSelection>, Action<ImportProgressEvent>, bool> ImportExecutor { get; set; }

        public DialogWindow()
        {
            InitializeComponent();
            
            Api = new EasyedaApi();
            cts = new CancellationTokenSource();
            previewCts = new CancellationTokenSource();
            searchResults = new ObservableCollection<PartInfoViewModel>();
            SelectedComponents = new List<ComponentSelection>();
            
            resultsGrid.ItemsSource = searchResults;
            _originalF3DPreview = CreateF3DPreviewHost("Original STEP");
            _cleanF3DPreview = CreateF3DPreviewHost("Clean STEP");
            f3dOriginalModelHost.Child = _originalF3DPreview.Panel;
            f3dCleanModelHost.Child = _cleanF3DPreview.Panel;
            LocationChanged += (s, e) => UpdateF3DPreviewScreenBounds();
            SizeChanged += (s, e) => UpdateF3DPreviewScreenBounds();
            Activated += (s, e) => UpdateAddButtonState();

            _footprintHelper = new CanvasZoomPanHelper(footprintCanvas);
            footprintCanvasView.ScrollChanged += (s, e) =>
            {
                if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
                    _footprintHelper.FitToBoundingBox();
            };

            _symbolHelper = new CanvasZoomPanHelper(symbolCanvas);
            symbolCanvasView.ScrollChanged += (s, e) =>
            {
                if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
                    _symbolHelper.FitToBoundingBox();
            };

        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            EasyEDALoaderModule.Trace("DialogWindow loaded.");
            ApplyAltiumTheme();
            SetSearchProgress(false);
            SetPreviewProgress(false);
            SetOperationProgress(false);
            RestoreLastSession();
            UpdateCleanTextControlState();
            searchTextBox.Focus();
            searchTextBox.CaretIndex = searchTextBox.Text?.Length ?? 0;
        }

        private void ApplyAltiumTheme()
        {
            var mainGrid = (System.Windows.Controls.Grid)this.Content;
            mainGrid.Background = Brushes.White;

            var textBrush = Brushes.Black;
            this.Resources[SystemColors.WindowTextBrushKey] = textBrush;
            ApplyColorToTextBlocks(mainGrid, textBrush);
        }

        private void ApplyColorToTextBlocks(System.Windows.DependencyObject parent, SolidColorBrush brush)
        {
            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                
                if (child is System.Windows.Controls.TextBlock textBlock)
                {
                    textBlock.Foreground = brush;
                }
                else if (child is System.Windows.Controls.ContentControl contentControl)
                {
                    contentControl.Foreground = brush;
                }
                
                // Recursively apply to children
                ApplyColorToTextBlocks(child, brush);
            }
        }

        private async void StepCleanOptionsChanged(object sender, RoutedEventArgs e)
        {
            UpdateCleanTextControlState();
            UpdateModelActionButtonState();

            if (_isRestoringSession)
                return;

            if (searchResults == null || resultsGrid == null)
                return;

            SaveLastSession();

            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = new CancellationTokenSource();

            if (resultsGrid?.SelectedItem is PartInfoViewModel partViewModel)
                await LoadPreviewAsync(partViewModel, previewCts.Token);
        }

        private void ImportOptionsChanged(object sender, RoutedEventArgs e)
        {
            if (_isRestoringSession)
                return;

            SaveLastSession();
        }

        private void UpdateCleanTextControlState()
        {
            if (cleanTextCheckBox == null)
                return;

            bool removeWatermarkSelected = removeWatermarkCheckBox?.IsChecked == true;
            cleanTextCheckBox.IsEnabled = removeWatermarkSelected &&
                removeWatermarkCheckBox?.IsEnabled == true &&
                !_isCriticalOperationActive;
            if (!removeWatermarkSelected)
                cleanTextCheckBox.IsChecked = false;
        }

        private void SetSearchProgress(bool isVisible, string message = null, double? progress = null)
        {
            searchProgressPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            searchProgressBar.IsIndeterminate = isVisible && !progress.HasValue;
            searchProgressBar.Value = isVisible && progress.HasValue ? progress.Value : 0;
            searchProgressText.Text = isVisible ? (message ?? "Searching...") : string.Empty;
        }

        private void SetPreviewProgress(bool isVisible, string message = null, double progress = 0)
        {
            previewProgressPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            previewProgressBar.IsIndeterminate = false;
            previewProgressBar.Value = isVisible ? progress : 0;
            previewProgressText.Text = isVisible ? (message ?? "Generating preview...") : string.Empty;
        }

        private void UpdatePreviewProgress(string message, double progress)
        {
            previewProgressPanel.Visibility = Visibility.Visible;
            previewProgressBar.IsIndeterminate = false;
            previewProgressBar.Value = progress;
            previewProgressText.Text = message;
        }

        private void SetOperationProgress(bool isVisible, string message = null, double? progress = null, bool isIndeterminate = true)
        {
            operationProgressPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            operationProgressBar.IsIndeterminate = isVisible && (isIndeterminate || !progress.HasValue);
            operationProgressBar.Value = isVisible && progress.HasValue ? progress.Value : 0;
            operationProgressText.Text = isVisible ? (message ?? "Working...") : string.Empty;
        }

        private void BeginCriticalOperation(string message)
        {
            _isCriticalOperationActive = true;
            _operationCompleted = false;
            operationLogTextBox.Clear();
            cancelButton.Content = "Working";
            cancelButton.IsEnabled = false;
            SetOperationProgress(true, message, null, true);
            AppendOperationLog(message, false);
            PumpUi();
        }

        private void CompleteCriticalOperation(string message, bool success)
        {
            _isCriticalOperationActive = false;
            _operationCompleted = success;
            cancelButton.Content = "Close";
            cancelButton.IsEnabled = true;
            SetOperationProgress(true, message, 100, false);
            AppendOperationLog(message, !success);
            PumpUi();
        }

        private void ReportImportProgress(ImportProgressEvent progress)
        {
            if (progress == null)
                return;

            SetOperationProgress(true, progress.Message, progress.Percent, progress.IsIndeterminate);
            if (progress.AddToLog)
                AppendOperationLog(progress.Message, progress.IsError);
            PumpUi();
        }

        private void AppendOperationLog(string message, bool isError)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string prefix = isError ? "ERROR " : "";
            operationLogTextBox.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + prefix + message + Environment.NewLine);
            operationLogTextBox.ScrollToEnd();
        }

        private void PumpUi()
        {
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Forms.Application.DoEvents();
        }

        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAddButtonState();

            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = new CancellationTokenSource();

            if (resultsGrid.SelectedItem is PartInfoViewModel partViewModel)
            {
                await LoadPreviewAsync(partViewModel, previewCts.Token);
            }
            else
            {
                ClearPreview();
            }
        }

        private async Task LoadPreviewAsync(PartInfoViewModel partViewModel, CancellationToken cancellationToken)
        {
            try
            {
                thumbnailImage.Source = null;
                symbolCanvas.Children.Clear();
                footprintCanvas.Children.Clear();
                ClearModelPreview();
                _currentComponent = null;
                _currentModel = null;
                _currentRoot = null;
                UpdateModelActionButtonState();
                SetPreviewProgress(true, "Loading component data...", 5);

                var root = await Task.Run(() => Api.GetComponentJsonAsync(partViewModel.PartInfo.Part, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                    return;

                if (root?.Component != null)
                {
                    _currentComponent = root.Component;
                    _currentRoot = root;

                    if (_currentComponent.Symbol?.Shapes != null)
                    {
                        UpdatePreviewProgress("Drawing symbol...", 30);
                        SymbolDrawing.DrawComponent(symbolCanvas, _currentComponent.Symbol.Shapes);
                        _ = symbolCanvas.Dispatcher.InvokeAsync(() =>
                        {
                            _symbolHelper.FitToBoundingBox();
                        }, DispatcherPriority.Loaded);
                    }

                    if (_currentComponent.PackageDetail?.Footprint != null)
                    {
                        UpdatePreviewProgress("Drawing footprint...", 60);
                        var eeFootprint = _currentComponent.PackageDetail.Footprint;
                        _currentModel = eeFootprint.GetModel();

                        UpdateModelActionButtonState();

                        EeFootprintContext ctx = new EeFootprintContext
                        {
                            Box = eeFootprint.BoundingBox,
                            Layers = eeFootprint.Layers,
                            CancelToken = cancellationToken,
                            Exception = null,
                        };

                        eeFootprint.DrawToCanvas(footprintCanvas, ctx);
                        
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        _ = footprintCanvas.Dispatcher.InvokeAsync(() =>
                        {
                            _footprintHelper.FitToBoundingBox();
                        }, DispatcherPriority.Loaded);

                        if (_currentModel != null)
                        {
                            try
                            {
                                UpdatePreviewProgress("Rendering 3D projection...", 72);
                                await ShowModelProjectionPreviewAsync(_currentModel, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                EasyEDALoaderModule.Trace($"3D projection preview failed: {ex}");
                            }

                            try
                            {
                                UpdatePreviewProgress("Loading 3D preview...", 78);
                                await ShowInteractiveModelPreviewAsync(_currentModel, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                EasyEDALoaderModule.Trace($"3D preview failed: {ex}");
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(_currentComponent.Thumb))
                    {
                        try
                        {
                            UpdatePreviewProgress("Loading thumbnail...", 85);
                            var thumbnail = await Task.Run(() => Api.LoadPngAsync(_currentComponent.Thumb, cancellationToken));
                            
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            if (thumbnail != null)
                            {
                                thumbnailImage.Source = thumbnail;
                                thumbnailImage.MaxWidth = thumbnail.Width;
                                thumbnailImage.MaxHeight = thumbnail.Height;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"Preview failed: {ex}");
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                    SetPreviewProgress(false);
            }
        }

        private void ClearPreview()
        {
            thumbnailImage.Source = null;
            symbolCanvas.Children.Clear();
            footprintCanvas.Children.Clear();
            ClearModelPreview();
            _currentComponent = null;
            _currentModel = null;
            _currentRoot = null;
            UpdateModelActionButtonState();
            SetPreviewProgress(false);
        }

        private void ClearModelPreview()
        {
            modelProjectionImage.Source = null;
            StopF3DPreview();
        }

        private async Task ShowModelProjectionPreviewAsync(EeFootprint3dModel modelInfo, CancellationToken cancellationToken)
        {
            modelProjectionImage.Source = null;

            if (modelInfo == null)
                return;

            byte[] stepData = await ModelCache.GetStepModelAsync(Api, modelInfo.Uuid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (stepData == null || stepData.Length == 0)
                return;

            var options = new StepProjectionOptions
            {
                ImageSizePixels = 256,
                PaddingPixels = 16,
                WriteMetadata = false
            };

            byte[] projectionPng = await Task.Run(() => StepProjectionRenderer.ProjectSingleViewPng(stepData, "z_plus", options), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (projectionPng == null || projectionPng.Length == 0)
                return;

            modelProjectionImage.Source = LoadBitmapImage(projectionPng);
        }

        private async Task ShowInteractiveModelPreviewAsync(EeFootprint3dModel modelInfo, CancellationToken cancellationToken)
        {
            StopF3DPreview();

            if (modelInfo == null)
                return;

            byte[] stepData = await ModelCache.GetStepModelAsync(Api, modelInfo.Uuid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (stepData == null || stepData.Length == 0)
                return;

            string stepPath = ModelCache.GetOriginalStepPath(modelInfo.Uuid);
            if (!File.Exists(stepPath))
            {
                string directory = Path.GetDirectoryName(stepPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(stepPath, stepData);
            }

            try
            {
                string cleanStepPath = await GetOrCreateCleanStepPreviewFileAsync(
                    modelInfo,
                    stepData,
                    RemoveWatermark,
                    CleanText,
                    cancellationToken);

                await StartF3DPreviewAsync(_originalF3DPreview, stepPath, false, cancellationToken);
                await StartF3DPreviewAsync(_cleanF3DPreview, cleanStepPath, false, cancellationToken);

                InstallF3DPreviewSync();
            }
            catch (StepWatermarkCleanFailedException ex)
            {
                ShowMarkdownReport(ex.ReportPath);
                EasyEDALoaderModule.Trace("Failed to create clean STEP preview: " + ex);
            }
        }

        private static BitmapImage LoadBitmapImage(byte[] imageData)
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(imageData))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            return bitmap;
        }

        private static async Task<string> GetOrCreateCleanStepPreviewFileAsync(
            EeFootprint3dModel modelInfo,
            byte[] originalStepData,
            bool removeWatermark,
            bool cleanText,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (originalStepData == null || originalStepData.Length == 0)
                throw new InvalidOperationException("Original STEP data is empty.");

            string modelKey = modelInfo?.Uuid ?? modelInfo?.Name;
            if (!removeWatermark)
                return ModelCache.GetOriginalStepPath(modelKey);

            string cleanModeKey = CleanStepCacheKeys.GetCleanModeKey(modelKey, cleanText);
            string safeName = ModelCache.GetSafeFileName(modelKey);
            ModelCacheResult cleanResult = await ModelCache.GetCleanStepModelWithStatusAsync(
                cleanModeKey,
                () => Task.Run(() =>
                    StepWatermarkCleanVerifier.CleanOrThrow(
                        originalStepData,
                        safeName,
                        Path.Combine(
                            ModelCache.GetLocalDataRoot(),
                            "StepCleanerReports",
                            safeName +
                            (cleanText ? "_text_" : "_") +
                            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)),
                        cleanText),
                    cancellationToken),
                cancellationToken);
            EasyEDALoaderModule.Trace(
                "Clean STEP cache " +
                (cleanResult.CacheHit ? "hit" : "miss") +
                ": model=" +
                safeName +
                " path=" +
                cleanResult.CachePath);

            cancellationToken.ThrowIfCancellationRequested();

            byte[] cleanStepData = cleanResult.Data;
            if (cleanStepData == null || cleanStepData.Length == 0)
                throw new InvalidOperationException("Clean STEP data is empty.");

            return ModelCache.GetCleanStepPath(cleanModeKey);
        }

        private F3DPreviewHost CreateF3DPreviewHost(string name)
        {
            var host = new F3DPreviewHost
            {
                Name = name,
                Panel = new Forms.Panel
                {
                    Dock = Forms.DockStyle.Fill,
                    BackColor = System.Drawing.Color.White
                }
            };

            host.Panel.Resize += (s, e) =>
            {
                ResizeEmbeddedF3DWindow(host);
                UpdateF3DPreviewScreenBounds(host);
            };
            return host;
        }

        private async Task StartF3DPreviewAsync(F3DPreviewHost preview, string stepPath, bool enableInput, CancellationToken cancellationToken)
        {
            string executable = FindF3DExecutable();
            if (string.IsNullOrEmpty(executable))
                throw new FileNotFoundException("F3D executable was not found. Install F3D or set STEPCLEANER_F3D to f3d.exe.");

            int width = Math.Max(320, preview.Panel?.ClientSize.Width ?? 0);
            int height = Math.Max(240, preview.Panel?.ClientSize.Height ?? 0);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            startInfo.ArgumentList.Add("--no-config");
            startInfo.ArgumentList.Add("--verbose=error");
            startInfo.ArgumentList.Add("--background-color");
            startInfo.ArgumentList.Add("#ffffff");
            startInfo.ArgumentList.Add("--resolution");
            startInfo.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture) + "," + height.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--position");
            startInfo.ArgumentList.Add("-32000,-32000");
            startInfo.ArgumentList.Add("--camera-orthographic");
            startInfo.ArgumentList.Add("--anti-aliasing=fxaa");
            startInfo.ArgumentList.Add("--ambient-occlusion");
            startInfo.ArgumentList.Add("--scalar-coloring");
            startInfo.ArgumentList.Add("--coloring-by-cells");
            startInfo.ArgumentList.Add("--coloring-array=Colors");
            startInfo.ArgumentList.Add("--coloring-component=-2");
            startInfo.ArgumentList.Add("--interaction-style=trackball");
            startInfo.ArgumentList.Add(stepPath);

            try
            {
                preview.Process = Process.Start(startInfo);
                if (preview.Process == null)
                    return;

                IntPtr handle = await WaitForMainWindowHandleAsync(preview.Process, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == IntPtr.Zero)
                {
                    StopF3DPreview(preview);
                    return;
                }

                preview.WindowHandle = handle;
                SetParent(preview.WindowHandle, preview.Panel.Handle);

                long style = GetWindowLongPtr(preview.WindowHandle, GwlStyle).ToInt64();
                long embeddedStyle = (style & ~(WsCaption | WsThickFrame | WsPopup)) | WsChild | WsVisible;
                SetWindowLongPtr(preview.WindowHandle, GwlStyle, new IntPtr(embeddedStyle));

                ShowWindow(preview.WindowHandle, SwShow);
                ResizeEmbeddedF3DWindow(preview);
                SetF3DPreviewEnabled(preview, enableInput);
            }
            catch (OperationCanceledException)
            {
                StopF3DPreview(preview);
                throw;
            }
        }

        private static async Task<IntPtr> WaitForMainWindowHandleAsync(Process process, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 200; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    return IntPtr.Zero;

                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;

                await Task.Delay(50, cancellationToken);
            }

            return IntPtr.Zero;
        }

        private static void ResizeEmbeddedF3DWindow(F3DPreviewHost preview)
        {
            if (preview?.WindowHandle == IntPtr.Zero || preview?.Panel == null)
                return;

            MoveWindow(
                preview.WindowHandle,
                0,
                0,
                Math.Max(1, preview.Panel.ClientSize.Width),
                Math.Max(1, preview.Panel.ClientSize.Height),
                true);
            SetWindowPos(
                preview.WindowHandle,
                IntPtr.Zero,
                0,
                0,
                Math.Max(1, preview.Panel.ClientSize.Width),
                Math.Max(1, preview.Panel.ClientSize.Height),
                SwpShowWindow);
        }

        private void StopF3DPreview()
        {
            UninstallF3DPreviewSync();
            StopF3DPreview(_originalF3DPreview);
            StopF3DPreview(_cleanF3DPreview);
        }

        private static void StopF3DPreview(F3DPreviewHost preview)
        {
            if (preview == null)
                return;

            preview.WindowHandle = IntPtr.Zero;
            preview.HasScreenBounds = false;
            if (preview.Process == null)
                return;

            try
            {
                if (!preview.Process.HasExited)
                {
                    preview.Process.CloseMainWindow();
                    if (!preview.Process.WaitForExit(800))
                        preview.Process.Kill();
                }
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to stop F3D preview " + preview.Name + ": " + ex);
            }
            finally
            {
                preview.Process.Dispose();
                preview.Process = null;
            }
        }

        private void SetF3DPreviewEnabled(F3DPreviewHost preview, bool enabled)
        {
            if (IsPreviewReady(preview))
                EnableWindow(preview.WindowHandle, enabled);
        }

        private void InstallF3DPreviewSync()
        {
            if (!IsPreviewReady(_originalF3DPreview) || !IsPreviewReady(_cleanF3DPreview))
                return;

            SetF3DPreviewEnabled(_originalF3DPreview, true);
            SetF3DPreviewEnabled(_cleanF3DPreview, true);
            _f3dMouseButtonState = 0;
            _f3dInputSource = null;
            UpdateF3DPreviewScreenBounds();
            _f3dInputReady = true;
            EnsureF3DRawInput();
        }

        private void UninstallF3DPreviewSync()
        {
            _f3dInputReady = false;
            _f3dMouseButtonState = 0;
            _f3dInputSource = null;
            RemoveF3DRawInput();
        }

        private void EnsureF3DRawInput()
        {
            if (_f3dRawInputRegistered)
                return;

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            _f3dRawInputSource = HwndSource.FromHwnd(handle);
            if (_f3dRawInputSource == null)
                return;

            var devices = new[]
            {
                new RawInputDevice
                {
                    UsagePage = HidUsagePageGeneric,
                    Usage = HidUsageGenericMouse,
                    Flags = RidevInputSink,
                    Target = handle
                }
            };

            if (!RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RawInputDevice>()))
            {
                EasyEDALoaderModule.Trace("Unable to register F3D raw input. Win32 error: " + Marshal.GetLastWin32Error());
                _f3dRawInputSource = null;
                return;
            }

            _f3dRawInputSource.AddHook(F3DRawInputWndProc);
            _f3dRawInputRegistered = true;
        }

        private void RemoveF3DRawInput()
        {
            if (_f3dRawInputRegistered)
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    var devices = new[]
                    {
                        new RawInputDevice
                        {
                            UsagePage = HidUsagePageGeneric,
                            Usage = HidUsageGenericMouse,
                            Flags = RidevRemove,
                            Target = IntPtr.Zero
                        }
                    };
                    RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RawInputDevice>());
                }

                _f3dRawInputRegistered = false;
            }

            if (_f3dRawInputSource != null)
            {
                _f3dRawInputSource.RemoveHook(F3DRawInputWndProc);
                _f3dRawInputSource = null;
            }
        }

        private IntPtr F3DRawInputWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmInput && CanMirrorF3DInput() && TryGetRawMouse(lParam, out RawMouse mouse))
                MirrorF3DRawMouseInput(mouse);

            return IntPtr.Zero;
        }

        private bool CanMirrorF3DInput()
        {
            return _f3dInputReady &&
                   _f3dRawInputRegistered &&
                   _originalF3DPreview?.WindowHandle != IntPtr.Zero &&
                   _cleanF3DPreview?.WindowHandle != IntPtr.Zero &&
                   _originalF3DPreview.HasScreenBounds &&
                   _cleanF3DPreview.HasScreenBounds;
        }

        private static bool TryGetRawMouse(IntPtr rawInputHandle, out RawMouse mouse)
        {
            mouse = default;
            int size = 0;
            int headerSize = Marshal.SizeOf<RawInputHeader>();
            int result = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
            if (result != 0 || size <= 0)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize);
                if (result != size)
                    return false;

                RawInput input = Marshal.PtrToStructure<RawInput>(buffer);
                if (input.Header.Type != RawInputMouse)
                    return false;

                mouse = input.Mouse;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void MirrorF3DRawMouseInput(RawMouse mouse)
        {
            if (!GetCursorPos(out NativePoint screenPoint))
                return;

            ushort buttonFlags = mouse.ButtonFlags;
            bool hasButtonDown =
                (buttonFlags & (RiMouseLeftButtonDown | RiMouseRightButtonDown | RiMouseMiddleButtonDown)) != 0;
            if (!hasButtonDown && ReleaseF3DDragIfPhysicalButtonsReleased(screenPoint))
                return;

            if ((buttonFlags & RiMouseLeftButtonDown) != 0)
                MirrorF3DMouseInput(WmLButtonDown, screenPoint, 0);
            if ((buttonFlags & RiMouseRightButtonDown) != 0)
                MirrorF3DMouseInput(WmRButtonDown, screenPoint, 0);
            if ((buttonFlags & RiMouseMiddleButtonDown) != 0)
                MirrorF3DMouseInput(WmMButtonDown, screenPoint, 0);

            if (mouse.LastX != 0 || mouse.LastY != 0)
                MirrorF3DMouseInput(WmMouseMove, screenPoint, 0);

            if ((buttonFlags & RiMouseWheel) != 0)
            {
                int delta = unchecked((short)mouse.ButtonData);
                MirrorF3DMouseInput(WmMouseWheel, screenPoint, delta);
            }

            if ((buttonFlags & RiMouseLeftButtonUp) != 0)
                MirrorF3DMouseInput(WmLButtonUp, screenPoint, 0);
            if ((buttonFlags & RiMouseRightButtonUp) != 0)
                MirrorF3DMouseInput(WmRButtonUp, screenPoint, 0);
            if ((buttonFlags & RiMouseMiddleButtonUp) != 0)
                MirrorF3DMouseInput(WmMButtonUp, screenPoint, 0);
        }

        private bool ReleaseF3DDragIfPhysicalButtonsReleased(NativePoint screenPoint)
        {
            if (_f3dInputSource == null || _f3dMouseButtonState == 0)
                return false;

            int physicalState = GetPhysicalF3DMouseButtonState();
            if ((_f3dMouseButtonState & ~physicalState) == 0)
                return false;

            ReleaseF3DDrag(_f3dInputSource, screenPoint);
            return true;
        }

        private static int GetPhysicalF3DMouseButtonState()
        {
            int state = 0;
            if (IsMouseButtonDown(VkLButton))
                state |= MkLButton;
            if (IsMouseButtonDown(VkRButton))
                state |= MkRButton;
            if (IsMouseButtonDown(VkMButton))
                state |= MkMButton;
            return state;
        }

        private static bool IsMouseButtonDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
        }

        private void MirrorF3DMouseInput(int message, NativePoint screenPoint, int wheelDelta)
        {
            if (_f3dInputSource != null &&
                message == WmMouseMove &&
                !PreviewContainsScreenPoint(_f3dInputSource, screenPoint))
            {
                ReleaseF3DDrag(_f3dInputSource, screenPoint);
                return;
            }

            F3DPreviewHost source = _f3dInputSource ?? GetPreviewUnderPoint(screenPoint);
            if (message == WmLButtonDown || message == WmRButtonDown || message == WmMButtonDown)
                source = GetPreviewUnderPoint(screenPoint);

            if (source == null)
                return;

            F3DPreviewHost target = ReferenceEquals(source, _originalF3DPreview)
                ? _cleanF3DPreview
                : _originalF3DPreview;
            if (!IsPreviewReady(target))
                return;

            if (message == WmLButtonDown)
                _f3dMouseButtonState |= MkLButton;
            else if (message == WmRButtonDown)
                _f3dMouseButtonState |= MkRButton;
            else if (message == WmMButtonDown)
                _f3dMouseButtonState |= MkMButton;
            else if (message == WmLButtonUp)
                _f3dMouseButtonState &= ~MkLButton;
            else if (message == WmRButtonUp)
                _f3dMouseButtonState &= ~MkRButton;
            else if (message == WmMButtonUp)
                _f3dMouseButtonState &= ~MkMButton;

            if (message == WmLButtonDown || message == WmRButtonDown || message == WmMButtonDown)
                _f3dInputSource = source;

            if (message == WmMouseMove && _f3dMouseButtonState == 0)
                return;

            if (message == WmMouseWheel)
            {
                SendMirroredF3DWheel(source, target, screenPoint, wheelDelta);
            }
            else if (message == WmMouseMove ||
                     message == WmLButtonDown ||
                     message == WmLButtonUp ||
                     message == WmRButtonDown ||
                     message == WmRButtonUp ||
                     message == WmMButtonDown ||
                     message == WmMButtonUp)
            {
                SendMirroredF3DMouseMessage(source, target, message, screenPoint);
            }

            if (_f3dMouseButtonState == 0 &&
                (message == WmLButtonUp || message == WmRButtonUp || message == WmMButtonUp))
            {
                _f3dInputSource = null;
            }
        }

        private void ReleaseF3DDrag(F3DPreviewHost source, NativePoint screenPoint)
        {
            if (source == null || _f3dMouseButtonState == 0)
                return;

            F3DPreviewHost target = ReferenceEquals(source, _originalF3DPreview)
                ? _cleanF3DPreview
                : _originalF3DPreview;

            int currentState = _f3dMouseButtonState;
            ReleaseF3DButton(source, target, WmLButtonUp, MkLButton, ref currentState, screenPoint);
            ReleaseF3DButton(source, target, WmRButtonUp, MkRButton, ref currentState, screenPoint);
            ReleaseF3DButton(source, target, WmMButtonUp, MkMButton, ref currentState, screenPoint);

            _f3dMouseButtonState = 0;
            _f3dInputSource = null;
        }

        private static void ReleaseF3DButton(
            F3DPreviewHost source,
            F3DPreviewHost target,
            int message,
            int buttonMask,
            ref int currentState,
            NativePoint screenPoint)
        {
            if ((currentState & buttonMask) == 0)
                return;

            currentState &= ~buttonMask;
            SendF3DMouseMessage(source, source, message, currentState, screenPoint);
            SendF3DMouseMessage(source, target, message, currentState, screenPoint);
        }

        private void SendMirroredF3DMouseMessage(
            F3DPreviewHost source,
            F3DPreviewHost target,
            int message,
            NativePoint screenPoint)
        {
            SendF3DMouseMessage(source, target, message, _f3dMouseButtonState, screenPoint);
        }

        private static void SendF3DMouseMessage(
            F3DPreviewHost source,
            F3DPreviewHost target,
            int message,
            int buttonState,
            NativePoint screenPoint)
        {
            if (source == null || !IsPreviewReady(target))
                return;

            NativePoint sourcePoint = screenPoint;
            ScreenToClient(source.WindowHandle, ref sourcePoint);
            NativePoint targetPoint = MapClientPointBetweenPreviews(source, target, sourcePoint);
            SendMessage(target.WindowHandle, message, new IntPtr(buttonState), MakeLParam(targetPoint.X, targetPoint.Y));
        }

        private void SendMirroredF3DWheel(F3DPreviewHost source, F3DPreviewHost target, NativePoint screenPoint, int delta)
        {
            NativePoint sourcePoint = screenPoint;
            ScreenToClient(source.WindowHandle, ref sourcePoint);
            NativePoint targetPoint = MapClientPointBetweenPreviews(source, target, sourcePoint);
            SendMessage(target.WindowHandle, WmMouseMove, new IntPtr(_f3dMouseButtonState), MakeLParam(targetPoint.X, targetPoint.Y));
            ClientToScreen(target.WindowHandle, ref targetPoint);
            SendMessage(target.WindowHandle, WmMouseWheel, MakeWParam(_f3dMouseButtonState, delta), MakeLParam(targetPoint.X, targetPoint.Y));
        }

        private F3DPreviewHost GetPreviewUnderPoint(NativePoint screenPoint)
        {
            if (PreviewContainsScreenPoint(_originalF3DPreview, screenPoint))
                return _originalF3DPreview;
            if (PreviewContainsScreenPoint(_cleanF3DPreview, screenPoint))
                return _cleanF3DPreview;
            return null;
        }

        private static bool PreviewContainsScreenPoint(F3DPreviewHost preview, NativePoint screenPoint)
        {
            if (preview == null || !preview.HasScreenBounds)
                return false;

            NativeRect rect = preview.ScreenBounds;

            return screenPoint.X >= rect.Left &&
                   screenPoint.X < rect.Right &&
                   screenPoint.Y >= rect.Top &&
                   screenPoint.Y < rect.Bottom;
        }

        private void UpdateF3DPreviewScreenBounds()
        {
            UpdateF3DPreviewScreenBounds(_originalF3DPreview);
            UpdateF3DPreviewScreenBounds(_cleanF3DPreview);
        }

        private static void UpdateF3DPreviewScreenBounds(F3DPreviewHost preview)
        {
            if (preview == null)
                return;

            preview.HasScreenBounds = false;

            if (preview.Panel != null && preview.Panel.IsHandleCreated)
            {
                System.Drawing.Rectangle rect = preview.Panel.RectangleToScreen(preview.Panel.ClientRectangle);
                if (rect.Width > 0 && rect.Height > 0)
                {
                    preview.ScreenBounds = new NativeRect
                    {
                        Left = rect.Left,
                        Top = rect.Top,
                        Right = rect.Right,
                        Bottom = rect.Bottom
                    };
                    preview.HasScreenBounds = true;
                    return;
                }
            }

            if (preview.WindowHandle != IntPtr.Zero && GetWindowRect(preview.WindowHandle, out NativeRect windowRect))
            {
                preview.ScreenBounds = windowRect;
                preview.HasScreenBounds = windowRect.Right > windowRect.Left && windowRect.Bottom > windowRect.Top;
            }
        }

        private static NativePoint MapClientPointBetweenPreviews(F3DPreviewHost source, F3DPreviewHost target, NativePoint sourcePoint)
        {
            GetClientSize(source.WindowHandle, out int sourceWidth, out int sourceHeight);
            GetClientSize(target.WindowHandle, out int targetWidth, out int targetHeight);

            return new NativePoint
            {
                X = Math.Max(0, Math.Min(targetWidth - 1, sourcePoint.X * targetWidth / sourceWidth)),
                Y = Math.Max(0, Math.Min(targetHeight - 1, sourcePoint.Y * targetHeight / sourceHeight))
            };
        }

        private static void GetClientSize(IntPtr windowHandle, out int width, out int height)
        {
            width = 1;
            height = 1;

            if (!GetClientRect(windowHandle, out NativeRect rect))
                return;

            width = Math.Max(1, rect.Right - rect.Left);
            height = Math.Max(1, rect.Bottom - rect.Top);
        }

        private static bool IsPreviewReady(F3DPreviewHost preview)
        {
            if (preview == null || preview.WindowHandle == IntPtr.Zero || preview.Process == null)
                return false;

            try
            {
                return !preview.Process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr MakeLParam(int low, int high)
        {
            return new IntPtr(((short)low & 0xffff) | (((short)high & 0xffff) << 16));
        }

        private static IntPtr MakeWParam(int low, int high)
        {
            return new IntPtr((low & 0xffff) | (((short)high & 0xffff) << 16));
        }

        private static string FindF3DExecutable()
        {
            string configuredPath = Environment.GetEnvironmentVariable("STEPCLEANER_F3D");
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(programFiles, "F3D", "bin", "f3d.exe"),
                Path.Combine(programFilesX86, "F3D", "bin", "f3d.exe")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return "f3d.exe";
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, value)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));
        }

        private sealed class F3DPreviewHost
        {
            public string Name { get; set; }
            public Forms.Panel Panel { get; set; }
            public Process Process { get; set; }
            public IntPtr WindowHandle { get; set; }
            public NativeRect ScreenBounds { get; set; }
            public bool HasScreenBounds { get; set; }
        }

        public void UpdateAddButtonState()
        {
            bool hasSelectedParts = searchResults.Any(p => p.AddToLibrary);
            addToLibraryButton.IsEnabled = hasSelectedParts;
            addFootprintButton.IsEnabled = hasSelectedParts && IsActivePcbLibrary();
            addSymbolButton.IsEnabled = hasSelectedParts && IsActiveSchLibrary();
            UpdateModelActionButtonState();
        }

        private void UpdateModelActionButtonState()
        {
            bool hasModel = _currentModel != null && !_isCriticalOperationActive;
            if (saveModelButton != null)
                saveModelButton.IsEnabled = hasModel;
            if (regenerateCleanStepButton != null)
                regenerateCleanStepButton.IsEnabled = hasModel && RemoveWatermark;
        }

        private static bool IsActivePcbLibrary()
        {
            try
            {
                return AltiumApi.GlobalVars.PCBServer?.GetCurrentPCBLibrary() != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsActiveSchLibrary()
        {
            try
            {
                var schDoc = AltiumApi.GlobalVars.SCHServer?.GetCurrentSchDocument();
                return schDoc != null && schDoc.GetState_ObjectId() == SCH.TObjectId.eSchLib;
            }
            catch
            {
                return false;
            }
        }

        private void RestoreLastSession()
        {
            if (!File.Exists(SessionStateFilePath))
                return;

            try
            {
                var json = File.ReadAllText(SessionStateFilePath);
                var state = JsonConvert.DeserializeObject<DialogSessionState>(json);
                if (state == null)
                    return;

                _isRestoringSession = true;

                searchTextBox.Text = state.SearchText ?? string.Empty;
                removeWatermarkCheckBox.IsChecked = state.RemoveWatermark ?? true;
                cleanTextCheckBox.IsChecked = state.CleanText ?? false;
                importLcscMechanicalLayersCheckBox.IsChecked = state.ImportLcscMechanicalLayers ?? false;
                UpdateCleanTextControlState();

                searchResults.Clear();
                if (state.Results != null)
                {
                    foreach (var result in state.Results)
                    {
                        if (result?.PartInfo == null)
                            continue;

                        var viewModel = new PartInfoViewModel(result.PartInfo, this)
                        {
                            AddToLibrary = result.AddToLibrary,
                            HasFootprint = result.PartInfo.HasFootprint,
                            Has3d = result.PartInfo.Has3d
                        };
                        searchResults.Add(viewModel);
                    }
                }

                if (searchResults.Count > 0)
                {
                    PartInfoViewModel selected = null;
                    if (!string.IsNullOrWhiteSpace(state.SelectedPart))
                    {
                        selected = searchResults.FirstOrDefault(result =>
                            string.Equals(result.PartInfo.Part, state.SelectedPart, StringComparison.OrdinalIgnoreCase));
                    }

                    resultsGrid.SelectedItem = selected ?? searchResults[0];
                    resultsGrid.ScrollIntoView(resultsGrid.SelectedItem);
                }

                UpdateAddButtonState();
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"Failed to restore dialog session: {ex}");
            }
            finally
            {
                _isRestoringSession = false;
            }
        }

        private void SaveLastSession()
        {
            if (_isRestoringSession)
                return;

            try
            {
                var state = new DialogSessionState
                {
                    SearchText = searchTextBox.Text,
                    RemoveWatermark = RemoveWatermark,
                    CleanText = CleanText,
                    ImportLcscMechanicalLayers = ImportLcscMechanicalLayers,
                    SelectedPart = (resultsGrid.SelectedItem as PartInfoViewModel)?.PartInfo?.Part,
                    Results = searchResults.Select(result => new DialogSessionResult
                    {
                        PartInfo = result.PartInfo,
                        AddToLibrary = result.AddToLibrary
                    }).ToList()
                };

                var directory = Path.GetDirectoryName(SessionStateFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(SessionStateFilePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"Failed to save dialog session: {ex}");
            }
        }

        private static List<string> ParsePartNumbers(string searchText)
        {
            return (searchText ?? string.Empty)
                .Split(PartNumberSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(partNumber => partNumber.Trim())
                .Where(partNumber => partNumber.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasImportReadyModels(EasyedaApi.PartInfo partInfo)
        {
            return partInfo?.HasFootprint == true && partInfo.Has3d;
        }

        private List<string> GetPartNumbersOrShowMessage()
        {
            var partNumbers = ParsePartNumbers(searchTextBox.Text);
            if (partNumbers.Count == 0)
                MessageBox.Show("Please enter at least one part number to search.", "Search Required", MessageBoxButton.OK, MessageBoxImage.Information);

            return partNumbers;
        }

        private async Task<List<PartInfoViewModel>> SearchPartNumbersAsync(IReadOnlyList<string> partNumbers, bool addToLibrary)
        {
            var viewModels = new List<PartInfoViewModel>();
            var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < partNumbers.Count; i++)
            {
                var partNumber = partNumbers[i];
                var startProgress = partNumbers.Count > 0 ? (i * 100.0) / partNumbers.Count : 0;
                SetSearchProgress(true, $"Searching {i + 1}/{partNumbers.Count}: {partNumber}", startProgress);

                var results = await Task.Run(() => Api.SearchProductInfoAsync(partNumber));
                if (results == null || results.Count == 0)
                    continue;

                foreach (var part in results)
                {
                    var key = part.Part ?? part.Name ?? $"{partNumber}:{viewModels.Count}";
                    if (!seenParts.Add(key))
                        continue;

                    viewModels.Add(new PartInfoViewModel(part, this)
                    {
                        AddToLibrary = addToLibrary && HasImportReadyModels(part)
                    });
                }

                var endProgress = ((i + 1) * 100.0) / partNumbers.Count;
                SetSearchProgress(true, $"Searched {i + 1}/{partNumbers.Count}", endProgress);
            }

            return viewModels;
        }

        private void AddSearchResults(IEnumerable<PartInfoViewModel> viewModels)
        {
            foreach (var viewModel in viewModels)
                searchResults.Add(viewModel);

            if (searchResults.Count > 0)
                resultsGrid.SelectedItem = searchResults[0];

            UpdateAddButtonState();
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            var partNumbers = GetPartNumbersOrShowMessage();
            if (partNumbers.Count == 0)
                return;

            searchButton.IsEnabled = false;
            addToLibraryButton.IsEnabled = false;
            addFootprintButton.IsEnabled = false;
            addSymbolButton.IsEnabled = false;
            searchResults.Clear();
            SetSearchProgress(true, "Searching EasyEDA...", 0);

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var results = await SearchPartNumbersAsync(partNumbers, true);

                if (results.Count > 0)
                {
                    SetSearchProgress(true, "Preparing results...", 100);
                    AddSearchResults(results);
                }
                else
                {
                    SetSearchProgress(false);
                    MessageBox.Show("No results found.", "Search", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Search failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetSearchProgress(false);
                Mouse.OverrideCursor = null;
                searchButton.IsEnabled = true;
                UpdateAddButtonState();
            }
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SearchButton_Click(sender, e);
            }
        }

        private async Task LoadComponentsForImportAsync(
            IReadOnlyList<PartInfoViewModel> selectedParts,
            ComponentImportTarget importTarget,
            bool includeFootprint,
            bool includeSymbol,
            bool include3dModel)
        {
            SelectedComponents.Clear();

            for (var i = 0; i < selectedParts.Count; i++)
            {
                var partViewModel = selectedParts[i];
                var partInfo = partViewModel.PartInfo;
                var progress = selectedParts.Count > 0 ? (i * 100.0) / selectedParts.Count : 0;
                SetSearchProgress(true, $"Loading component {i + 1}/{selectedParts.Count}: {partInfo.Part}", progress);
                ReportImportProgress(new ImportProgressEvent
                {
                    Message = $"Loading component data {i + 1}/{selectedParts.Count}: {partInfo.Part}",
                    Percent = Math.Min(20, progress * 0.2),
                    IsIndeterminate = false
                });

                var root = await Task.Run(() => Api.GetComponentJsonAsync(partInfo.Part, cts.Token));

                if (root?.Component != null)
                {
                    var component = root.Component;
                    var has3dModel = component.PackageDetail?.Footprint?.GetModel() != null;
                    var hasFootprint = component.PackageDetail?.Footprint != null;
                    var hasSymbol = component.Symbol != null;
                    var includeThisFootprint = includeFootprint && hasFootprint;
                    var includeThisSymbol = includeSymbol && hasSymbol;

                    partViewModel.HasFootprint = hasFootprint;
                    partViewModel.Has3d = has3dModel;

                    if (!includeThisFootprint && !includeThisSymbol)
                        continue;

                    SelectedComponents.Add(new ComponentSelection
                    {
                        PartInfo = partInfo,
                        Root = root,
                        Include3dModel = include3dModel && includeThisFootprint && has3dModel,
                        IncludeFootprint = includeThisFootprint,
                        IncludeSymbol = includeThisSymbol,
                        RemoveWatermark = RemoveWatermark,
                        CleanText = CleanText,
                        ImportLcscMechanicalLayers = ImportLcscMechanicalLayers,
                        ImportTarget = importTarget
                    });
                }
            }

            if (SelectedComponents.Count == 0)
                throw new InvalidOperationException("No component data was loaded.");

            SetSearchProgress(true, "Import ready.", 100);
            ReportImportProgress(new ImportProgressEvent
            {
                Message = "Component data loaded.",
                Percent = 20,
                IsIndeterminate = false
            });
        }

        private void SetImportControlsEnabled(bool isEnabled)
        {
            searchButton.IsEnabled = isEnabled;
            if (isEnabled)
            {
                UpdateAddButtonState();
            }
            else
            {
                addToLibraryButton.IsEnabled = false;
                addFootprintButton.IsEnabled = false;
                addSymbolButton.IsEnabled = false;
                if (saveModelButton != null)
                    saveModelButton.IsEnabled = false;
                if (regenerateCleanStepButton != null)
                    regenerateCleanStepButton.IsEnabled = false;
            }
            cancelButton.IsEnabled = isEnabled;
            removeWatermarkCheckBox.IsEnabled = isEnabled;
            importLcscMechanicalLayersCheckBox.IsEnabled = isEnabled;
            UpdateCleanTextControlState();
            if (isEnabled)
                UpdateModelActionButtonState();
        }

        private bool ValidateImportOptions()
        {
            return true;
        }

        private async void AddToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            await ImportSelectedPartsAsync(
                ComponentImportTarget.TemporaryLibraries,
                includeFootprint: true,
                includeSymbol: true,
                include3dModel: true,
                inactiveTargetMessage: null);
        }

        private async void AddFootprintButton_Click(object sender, RoutedEventArgs e)
        {
            await ImportSelectedPartsAsync(
                ComponentImportTarget.ActivePcbLibrary,
                includeFootprint: true,
                includeSymbol: false,
                include3dModel: true,
                inactiveTargetMessage: "Open and activate a PCB library before adding a footprint.");
        }

        private async void AddSymbolButton_Click(object sender, RoutedEventArgs e)
        {
            await ImportSelectedPartsAsync(
                ComponentImportTarget.ActiveSchLibrary,
                includeFootprint: false,
                includeSymbol: true,
                include3dModel: false,
                inactiveTargetMessage: "Open and activate a schematic library before adding a symbol.");
        }

        private async Task ImportSelectedPartsAsync(
            ComponentImportTarget importTarget,
            bool includeFootprint,
            bool includeSymbol,
            bool include3dModel,
            string inactiveTargetMessage)
        {
            var selectedParts = searchResults.Where(p => p.AddToLibrary).ToList();

            if (importTarget == ComponentImportTarget.ActivePcbLibrary && !IsActivePcbLibrary())
            {
                MessageBox.Show(inactiveTargetMessage, "No Active PCB Library", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateAddButtonState();
                return;
            }

            if (importTarget == ComponentImportTarget.ActiveSchLibrary && !IsActiveSchLibrary())
            {
                MessageBox.Show(inactiveTargetMessage, "No Active Schematic Library", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateAddButtonState();
                return;
            }

            if (selectedParts.Count == 0)
            {
                MessageBox.Show("Please select at least one component to add.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ValidateImportOptions())
                return;

            var closeDialog = false;
            SetImportControlsEnabled(false);

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                BeginCriticalOperation("Preparing import...");
                await LoadComponentsForImportAsync(selectedParts, importTarget, includeFootprint, includeSymbol, include3dModel);

                if (ImportExecutor == null)
                    throw new InvalidOperationException("Import executor is not available.");

                ReportImportProgress(new ImportProgressEvent
                {
                    Message = "Starting Altium import...",
                    Percent = 20,
                    IsIndeterminate = false
                });

                bool imported = ImportExecutor(SelectedComponents, ReportImportProgress);
                closeDialog = imported;
                CompleteCriticalOperation(imported ? "Import complete." : "Import did not complete.", imported);
            }
            catch (Exception ex)
            {
                CompleteCriticalOperation($"Import failed: {ex.Message}", false);
                MessageBox.Show($"Failed to load component data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                if (!closeDialog)
                {
                    SetSearchProgress(false);
                    SetImportControlsEnabled(true);
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCriticalOperationActive)
                return;

            DialogResult = _operationCompleted;
            Close();
        }

        private async void SaveModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null)
                return;

            var saveFileDialog = new SaveFileDialog
            {
                Title = "Save Model File As",
                Filter = "STEP Files (*.step)|*.step|All Files (*.*)|*.*",
                FileName = $"{_currentModel.Name}.step",
                DefaultExt = "step"
            };

            bool? result = saveFileDialog.ShowDialog();
            if (result == true)
            {
                saveModelButton.IsEnabled = false;
                regenerateCleanStepButton.IsEnabled = false;
                try
                {
                    Mouse.OverrideCursor = Cursors.Wait;
                    SetImportControlsEnabled(false);
                    BeginCriticalOperation("Saving STEP model...");

                    var modelData = await GetSelectedStepModelForSaveAsync(_currentModel, cts.Token, (message, progress) =>
                    {
                        ReportImportProgress(new ImportProgressEvent
                        {
                            Message = message,
                            Percent = progress,
                            IsIndeterminate = !progress.HasValue
                        });
                    });

                    if (modelData != null && modelData.Length > 0)
                    {
                        ReportImportProgress(new ImportProgressEvent
                        {
                            Message = "Writing STEP file...",
                            Percent = 95,
                            IsIndeterminate = false
                        });
                        File.WriteAllBytes(saveFileDialog.FileName, modelData);
                        CompleteCriticalOperation($"Model saved to {saveFileDialog.FileName}", true);
                        MessageBox.Show($"Model saved successfully to {saveFileDialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        CompleteCriticalOperation("Model save failed: downloaded data was empty.", false);
                        MessageBox.Show("Failed to download model data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (StepWatermarkCleanFailedException ex)
                {
                    CompleteCriticalOperation($"Failed to clean model: {ex.Message}", false);
                    ShowMarkdownReport(ex.ReportPath);
                    MessageBox.Show($"Failed to clean model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    CompleteCriticalOperation($"Failed to save model: {ex.Message}", false);
                    MessageBox.Show($"Failed to save model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    SetImportControlsEnabled(true);
                    UpdateModelActionButtonState();
                }
            }
        }

        private async void RegenerateCleanStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentModel == null || !RemoveWatermark)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetImportControlsEnabled(false);
                BeginCriticalOperation("Regenerating cleaned STEP cache...");

                string modelKey = _currentModel.Uuid ?? _currentModel.Name;
                int deletedCount = ModelCache.DeleteCleanStepModels(modelKey);
                ReportImportProgress(new ImportProgressEvent
                {
                    Message = "Deleted " + deletedCount.ToString(CultureInfo.InvariantCulture) + " cleaned STEP cache file(s).",
                    Percent = 20,
                    IsIndeterminate = false
                });

                previewCts?.Cancel();
                previewCts?.Dispose();
                previewCts = new CancellationTokenSource();

                if (resultsGrid?.SelectedItem is PartInfoViewModel partViewModel)
                    await LoadPreviewAsync(partViewModel, previewCts.Token);

                CompleteCriticalOperation("Cleaned STEP cache regenerated.", true);
            }
            catch (StepWatermarkCleanFailedException ex)
            {
                CompleteCriticalOperation($"Failed to regenerate cleaned STEP cache: {ex.Message}", false);
                ShowMarkdownReport(ex.ReportPath);
                MessageBox.Show($"Failed to regenerate cleaned STEP cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                CompleteCriticalOperation($"Failed to regenerate cleaned STEP cache: {ex.Message}", false);
                MessageBox.Show($"Failed to regenerate cleaned STEP cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                SetImportControlsEnabled(true);
                UpdateModelActionButtonState();
            }
        }

        private async Task<byte[]> GetSelectedStepModelForSaveAsync(
            EeFootprint3dModel modelInfo,
            CancellationToken cancellationToken,
            Action<string, double?> progress)
        {
            progress?.Invoke("Downloading STEP model...", 20);
            byte[] originalModel = await ModelCache.GetStepModelAsync(Api, modelInfo.Uuid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!RemoveWatermark)
            {
                progress?.Invoke("Using original STEP model.", 90);
                return originalModel;
            }

            progress?.Invoke("Cleaning STEP watermark geometry...", null);
            return await Task.Run(() =>
                StepWatermarkCleanVerifier.CleanOrThrow(
                    originalModel,
                    ModelCache.GetSafeFileName(modelInfo.Uuid ?? modelInfo.Name),
                    Path.Combine(
                        ModelCache.GetLocalDataRoot(),
                        "StepCleanerReports",
                        ModelCache.GetSafeFileName(modelInfo.Uuid ?? modelInfo.Name) +
                        (CleanText ? "_text" : string.Empty) +
                        "_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)),
                    CleanText),
                cancellationToken);
        }

        private static void ShowMarkdownReport(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to open StepCleaner report: " + ex);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isCriticalOperationActive)
            {
                e.Cancel = true;
                AppendOperationLog("Close ignored while operation is running.", false);
                PumpUi();
                return;
            }

            SaveLastSession();
            cts?.Cancel();
            previewCts?.Cancel();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            StopF3DPreview();
            cts?.Dispose();
            previewCts?.Dispose();
            base.OnClosed(e);
        }
    }

    public class PartInfoViewModel : INotifyPropertyChanged
    {
        private bool _addToLibrary;
        private bool _hasFootprint;
        private bool _has3d;
        private readonly DialogWindow _parentWindow;

        public EasyedaApi.PartInfo PartInfo { get; }

        public bool AddToLibrary
        {
            get => _addToLibrary;
            set
            {
                if (_addToLibrary != value)
                {
                    _addToLibrary = value;
                    OnPropertyChanged(nameof(AddToLibrary));
                    _parentWindow?.UpdateAddButtonState();
                }
            }
        }

        public bool HasFootprint
        {
            get => _hasFootprint;
            set
            {
                if (_hasFootprint != value)
                {
                    _hasFootprint = value;
                    OnPropertyChanged(nameof(HasFootprint));
                }
            }
        }

        public bool Has3d
        {
            get => _has3d;
            set
            {
                if (_has3d != value)
                {
                    _has3d = value;
                    OnPropertyChanged(nameof(Has3d));
                }
            }
        }

        public string Name => PartInfo.Name ?? PartInfo.Part;
        public string Description => PartInfo.Description ?? "";

        public PartInfoViewModel(EasyedaApi.PartInfo partInfo, DialogWindow parentWindow)
        {
            PartInfo = partInfo;
            _parentWindow = parentWindow;
            _hasFootprint = partInfo.HasFootprint;
            _has3d = partInfo.Has3d;
            _addToLibrary = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed class DialogSessionState
    {
        public string SearchText { get; set; }
        public bool? RemoveWatermark { get; set; }
        public bool? CleanText { get; set; }
        public bool? ImportLcscMechanicalLayers { get; set; }
        public string SelectedPart { get; set; }
        public List<DialogSessionResult> Results { get; set; }
    }

    internal sealed class DialogSessionResult
    {
        public EasyedaApi.PartInfo PartInfo { get; set; }
        public bool AddToLibrary { get; set; }
    }
}
