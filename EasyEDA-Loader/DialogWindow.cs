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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
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
        private Forms.Panel _f3dPanel;
        private Process _f3dProcess;
        private IntPtr _f3dWindowHandle;
        private const int GwlStyle = -16;
        private const int SwShow = 5;
        private const long WsChild = 0x40000000L;
        private const long WsVisible = 0x10000000L;
        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsPopup = unchecked((long)0x80000000L);
        private static readonly char[] PartNumberSeparators = { '\r', '\n', '\t', ' ', ',', ';', '|' };
        private static readonly string SessionStateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyEDA-Loader",
            "dialog-session.json");

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

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

        public DialogWindow()
        {
            InitializeComponent();
            
            Api = new EasyedaApi();
            cts = new CancellationTokenSource();
            previewCts = new CancellationTokenSource();
            searchResults = new ObservableCollection<PartInfoViewModel>();
            SelectedComponents = new List<ComponentSelection>();
            
            resultsGrid.ItemsSource = searchResults;
            _f3dPanel = new Forms.Panel
            {
                Dock = Forms.DockStyle.Fill,
                BackColor = System.Drawing.Color.White
            };
            _f3dPanel.Resize += (s, e) => ResizeEmbeddedF3DWindow();
            f3dModelHost.Child = _f3dPanel;

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
            RestoreLastSession();
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
                saveModelButton.IsEnabled = false;
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

                        saveModelButton.IsEnabled = _currentModel != null;

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
            saveModelButton.IsEnabled = false;
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

            await StartF3DPreviewAsync(stepPath, cancellationToken);
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

        private async Task StartF3DPreviewAsync(string stepPath, CancellationToken cancellationToken)
        {
            string executable = FindF3DExecutable();
            if (string.IsNullOrEmpty(executable))
                throw new FileNotFoundException("F3D executable was not found. Install F3D or set STEPCLEANER_F3D to f3d.exe.");

            int width = Math.Max(320, _f3dPanel?.ClientSize.Width ?? 0);
            int height = Math.Max(240, _f3dPanel?.ClientSize.Height ?? 0);

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
                _f3dProcess = Process.Start(startInfo);
                if (_f3dProcess == null)
                    return;

                IntPtr handle = await WaitForMainWindowHandleAsync(_f3dProcess, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == IntPtr.Zero)
                {
                    StopF3DPreview();
                    return;
                }

                _f3dWindowHandle = handle;
                SetParent(_f3dWindowHandle, _f3dPanel.Handle);

                long style = GetWindowLongPtr(_f3dWindowHandle, GwlStyle).ToInt64();
                long embeddedStyle = (style & ~(WsCaption | WsThickFrame | WsPopup)) | WsChild | WsVisible;
                SetWindowLongPtr(_f3dWindowHandle, GwlStyle, new IntPtr(embeddedStyle));

                ShowWindow(_f3dWindowHandle, SwShow);
                ResizeEmbeddedF3DWindow();
            }
            catch (OperationCanceledException)
            {
                StopF3DPreview();
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

        private void ResizeEmbeddedF3DWindow()
        {
            if (_f3dWindowHandle == IntPtr.Zero || _f3dPanel == null)
                return;

            MoveWindow(
                _f3dWindowHandle,
                0,
                0,
                Math.Max(1, _f3dPanel.ClientSize.Width),
                Math.Max(1, _f3dPanel.ClientSize.Height),
                true);
            SetWindowPos(
                _f3dWindowHandle,
                IntPtr.Zero,
                0,
                0,
                Math.Max(1, _f3dPanel.ClientSize.Width),
                Math.Max(1, _f3dPanel.ClientSize.Height),
                0x0040);
        }

        private void StopF3DPreview()
        {
            _f3dWindowHandle = IntPtr.Zero;
            if (_f3dProcess == null)
                return;

            try
            {
                if (!_f3dProcess.HasExited)
                {
                    _f3dProcess.CloseMainWindow();
                    if (!_f3dProcess.WaitForExit(800))
                        _f3dProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to stop F3D preview: " + ex);
            }
            finally
            {
                _f3dProcess.Dispose();
                _f3dProcess = null;
            }
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

        public void UpdateAddButtonState()
        {
            addToLibraryButton.IsEnabled = searchResults.Any(p => p.AddToLibrary);
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
            importButton.IsEnabled = false;
            addToLibraryButton.IsEnabled = false;
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
                importButton.IsEnabled = true;
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

        private async Task LoadComponentsForImportAsync(IReadOnlyList<PartInfoViewModel> selectedParts)
        {
            SelectedComponents.Clear();

            for (var i = 0; i < selectedParts.Count; i++)
            {
                var partViewModel = selectedParts[i];
                var partInfo = partViewModel.PartInfo;
                var progress = selectedParts.Count > 0 ? (i * 100.0) / selectedParts.Count : 0;
                SetSearchProgress(true, $"Loading component {i + 1}/{selectedParts.Count}: {partInfo.Part}", progress);

                var root = await Task.Run(() => Api.GetComponentJsonAsync(partInfo.Part, cts.Token));

                if (root?.Component != null)
                {
                    var component = root.Component;
                    var has3dModel = component.PackageDetail?.Footprint?.GetModel() != null;
                    var hasFootprint = component.PackageDetail?.Footprint != null;

                    partViewModel.HasFootprint = hasFootprint;
                    partViewModel.Has3d = has3dModel;

                    SelectedComponents.Add(new ComponentSelection
                    {
                        PartInfo = partInfo,
                        Root = root,
                        Include3dModel = has3dModel,
                        IncludeFootprint = hasFootprint,
                        RemoveWatermark = RemoveWatermark
                    });
                }
            }

            if (SelectedComponents.Count == 0)
                throw new InvalidOperationException("No component data was loaded.");

            SetSearchProgress(true, "Import ready.", 100);
        }

        private void SetImportControlsEnabled(bool isEnabled)
        {
            searchButton.IsEnabled = isEnabled;
            importButton.IsEnabled = isEnabled;
            addToLibraryButton.IsEnabled = isEnabled && searchResults.Any(p => p.AddToLibrary);
            cancelButton.IsEnabled = isEnabled;
            removeWatermarkCheckBox.IsEnabled = isEnabled;
        }

        private bool ValidateImportOptions()
        {
            return true;
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var partNumbers = GetPartNumbersOrShowMessage();
            if (partNumbers.Count == 0)
                return;
            if (!ValidateImportOptions())
                return;

            var closeDialog = false;
            SetImportControlsEnabled(false);
            searchResults.Clear();

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetSearchProgress(true, "Searching EasyEDA...", 0);

                var results = await SearchPartNumbersAsync(partNumbers, true);
                if (results.Count == 0)
                {
                    MessageBox.Show("No results found.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SetSearchProgress(true, "Preparing import...", 100);
                AddSearchResults(results);

                var selectedResults = results.Where(result => result.AddToLibrary).ToList();
                if (selectedResults.Count == 0)
                {
                    MessageBox.Show("No results with both footprint and 3D body were found.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await LoadComponentsForImportAsync(selectedResults);

                closeDialog = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
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

        private async void AddToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedParts = searchResults.Where(p => p.AddToLibrary).ToList();

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
                await LoadComponentsForImportAsync(selectedParts);

                closeDialog = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
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
            DialogResult = false;
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
                try
                {
                    Mouse.OverrideCursor = Cursors.Wait;

                    var modelData = await GetSelectedStepModelForSaveAsync(_currentModel, cts.Token);

                    if (modelData != null && modelData.Length > 0)
                    {
                        File.WriteAllBytes(saveFileDialog.FileName, modelData);
                        MessageBox.Show($"Model saved successfully to {saveFileDialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("Failed to download model data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (StepWatermarkCleanFailedException ex)
                {
                    ShowMarkdownReport(ex.ReportPath);
                    MessageBox.Show($"Failed to clean model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    saveModelButton.IsEnabled = _currentModel != null;
                }
            }
        }

        private async Task<byte[]> GetSelectedStepModelForSaveAsync(EeFootprint3dModel modelInfo, CancellationToken cancellationToken)
        {
            byte[] originalModel = await ModelCache.GetStepModelAsync(Api, modelInfo.Uuid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!RemoveWatermark)
                return originalModel;

            return await Task.Run(() =>
                StepWatermarkCleanVerifier.CleanOrThrow(
                    originalModel,
                    ModelCache.GetSafeFileName(modelInfo.Uuid ?? modelInfo.Name),
                    Path.Combine(
                        ModelCache.GetLocalDataRoot(),
                        "StepCleanerReports",
                        ModelCache.GetSafeFileName(modelInfo.Uuid ?? modelInfo.Name) +
                        "_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture))),
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
        public string SelectedPart { get; set; }
        public List<DialogSessionResult> Results { get; set; }
    }

    internal sealed class DialogSessionResult
    {
        public EasyedaApi.PartInfo PartInfo { get; set; }
        public bool AddToLibrary { get; set; }
    }
}
