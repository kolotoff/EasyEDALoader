using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using PCB;
using SCH;
using Forms = System.Windows.Forms;
using StepF3DRenderLib;

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
        private string _currentPreviewPartNumber;
        private bool _isRestoringSession;
        private F3DProjectionRenderer.F3DPreviewSession _f3dPreviewSession;
        private F3DPreviewCameraSnapshot _f3dPreviewCameraSnapshot;
        private CancellationTokenSource _f3dPreviewRenderCts;
        private int _f3dPreviewRenderRequestId;
        private readonly object _f3dPreviewInteractionLock = new object();
        private readonly List<F3DPreviewInteraction> _f3dPendingInteractions = new List<F3DPreviewInteraction>();
        private bool _f3dPreviewRenderScheduled;
        private bool _f3dPreviewRenderRunning;
        private bool _f3dPreviewRenderAgain;
        private Point? _f3dPreviewDragStart;
        private MouseButton? _f3dPreviewDragButton;
        private bool _isCriticalOperationActive;
        private bool _operationCompleted;
        private static readonly char[] PartNumberSeparators = { '\r', '\n', '\t', ' ', ',', ';', '|' };
        private static readonly string SessionStateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyEDA-Loader",
            "dialog-session.json");

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

            if (_currentModel != null)
            {
                try
                {
                    UpdatePreviewProgress("Updating 3D preview...", 78);
                    await ShowInteractiveModelPreviewAsync(_currentModel, previewCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace($"3D preview update failed: {ex}");
                }
                finally
                {
                    if (!previewCts.IsCancellationRequested)
                        SetPreviewProgress(false);
                }

                return;
            }

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
                _currentPreviewPartNumber = null;
                UpdateModelActionButtonState();
                SetPreviewProgress(true, "Loading component data...", 5);

                var root = await ModelCache.GetComponentJsonAsync(Api, partViewModel.PartInfo.Part, cancellationToken);

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
                        _currentPreviewPartNumber = partViewModel.PartInfo.Part;

                        UpdateModelActionButtonState();

                        Task<byte[]> stepDataTask = null;
                        if (_currentModel != null)
                        {
                            UpdatePreviewProgress("Loading 3D preview...", 62);
                            stepDataTask = ModelCache.GetStepModelAsync(Api, _currentModel.Uuid, cancellationToken);
                            Task interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask, cancellationToken);
                            ObservePreviewTask(interactivePreviewTask, "3D preview");
                        }

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
                                await ShowModelProjectionPreviewAsync(_currentModel, partViewModel, stepDataTask, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                EasyEDALoaderModule.Trace($"3D projection preview failed: {ex}");
                            }

                        }
                    }

                    if (!string.IsNullOrEmpty(_currentComponent.Thumb))
                    {
                        try
                        {
                            UpdatePreviewProgress("Loading thumbnail...", 85);
                            byte[] thumbnailData = await ModelCache.GetPngImageAsync(
                                Api,
                                _currentComponent.Thumb,
                                partViewModel.PartInfo.Part,
                                cancellationToken);
                            
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            if (thumbnailData != null && thumbnailData.Length > 0)
                            {
                                var thumbnail = LoadBitmapImage(thumbnailData);
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

        private static void ObservePreviewTask(Task task, string operationName)
        {
            if (task == null)
                return;

            _ = task.ContinueWith(
                completedTask =>
                {
                    if (completedTask.Exception != null)
                        EasyEDALoaderModule.Trace(operationName + " failed: " + completedTask.Exception.GetBaseException());
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
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
            _currentPreviewPartNumber = null;
            UpdateModelActionButtonState();
            SetPreviewProgress(false);
        }

        private void ClearModelPreview()
        {
            modelProjectionImage.Source = null;
            _f3dPreviewCameraSnapshot = null;
            StopF3DPreview();
        }

        private async Task ShowModelProjectionPreviewAsync(
            EeFootprint3dModel modelInfo,
            PartInfoViewModel partViewModel,
            Task<byte[]> originalStepDataTask,
            CancellationToken cancellationToken)
        {
            modelProjectionImage.Source = null;

            if (modelInfo == null)
                return;

            GetModelProjectionPreviewImageSizePixels(out int imageWidthPixels, out int imageHeightPixels);
            var options = new StepProjectionOptions
            {
                ImageSizePixels = Math.Max(256, Math.Max(imageWidthPixels, imageHeightPixels)),
                ImageWidthPixels = imageWidthPixels,
                ImageHeightPixels = imageHeightPixels,
                PaddingPixels = 16,
                WriteMetadata = false
            };

            string selectedComponentCacheKey = GetSelectedComponentCacheKey(partViewModel, modelInfo);
            byte[] projectionPng = await ModelCache.GetProjectionPreviewPngAsync(
                selectedComponentCacheKey,
                modelInfo.Uuid,
                imageWidthPixels,
                imageHeightPixels,
                async () =>
                {
                    byte[] originalStepData = originalStepDataTask == null
                        ? await ModelCache.GetStepModelAsync(Api, modelInfo.Uuid, cancellationToken).ConfigureAwait(false)
                        : await originalStepDataTask.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (originalStepData == null || originalStepData.Length == 0)
                        return null;

                    return await Task.Run(
                        () => StepProjectionRenderer.ProjectSingleViewPng(originalStepData, "z_plus", options),
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (projectionPng == null || projectionPng.Length == 0)
                return;

            modelProjectionImage.Source = LoadBitmapImage(projectionPng);
        }

        private void GetModelProjectionPreviewImageSizePixels(out int imageWidthPixels, out int imageHeightPixels)
        {
            double viewportWidth = modelProjectionViewport == null ? 0.0 : modelProjectionViewport.ActualWidth;
            double viewportHeight = modelProjectionViewport == null ? 0.0 : modelProjectionViewport.ActualHeight;
            if (double.IsNaN(viewportWidth) || double.IsInfinity(viewportWidth) || viewportWidth <= 0.0)
                viewportWidth = 256.0;
            if (double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight) || viewportHeight <= 0.0)
                viewportHeight = 160.0;

            imageWidthPixels = Math.Max(160, (int)Math.Round(viewportWidth));
            imageHeightPixels = Math.Max(120, (int)Math.Round(viewportHeight));
        }

        private async Task ShowInteractiveModelPreviewAsync(EeFootprint3dModel modelInfo, CancellationToken cancellationToken)
        {
            if (modelInfo == null)
                return;

            Task<byte[]> stepDataTask = ModelCache.GetStepModelAsync(Api, modelInfo.Uuid, cancellationToken);
            await ShowInteractiveModelPreviewAsync(modelInfo, stepDataTask, cancellationToken);
        }

        private async Task ShowInteractiveModelPreviewAsync(
            EeFootprint3dModel modelInfo,
            Task<byte[]> stepDataTask,
            CancellationToken cancellationToken)
        {
            F3DPreviewCameraSnapshot cameraSnapshot = CaptureF3DPreviewCameraSnapshot();
            StopF3DPreview();

            if (modelInfo == null)
                return;

            byte[] stepData = await stepDataTask.ConfigureAwait(true);
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
                bool removeWatermark = RemoveWatermark;
                bool cleanText = CleanText;
                string selectedComponentCacheKey = GetSelectedComponentCacheKey(resultsGrid?.SelectedItem as PartInfoViewModel, modelInfo);
                byte[] previewStepData = removeWatermark
                    ? await GetOrCreateCleanStepPreviewDataAsync(
                        modelInfo,
                        selectedComponentCacheKey,
                        stepData,
                        removeWatermark,
                        cleanText,
                        cancellationToken)
                    : stepData;
                if (f3dPreviewTitleTextBlock != null)
                    f3dPreviewTitleTextBlock.Text = removeWatermark ? "Clean STEP" : "Original STEP";

                F3DProjectionRenderer.F3DPreviewSession previewSession = await Task.Run(
                    () => F3DProjectionRenderer.CreatePreviewSession(previewStepData, cameraSnapshot),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _f3dPreviewSession = previewSession;
                QueueF3DPreviewRender();
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

        private static async Task<byte[]> GetOrCreateCleanStepPreviewDataAsync(
            EeFootprint3dModel modelInfo,
            string selectedComponentCacheKey,
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
                return originalStepData;

            string cleanCacheBaseKey = FirstNonEmpty(selectedComponentCacheKey, modelKey);
            string cleanModeKey = CleanStepCacheKeys.GetCleanModeKey(cleanCacheBaseKey, cleanText);
            string safeName = ModelCache.GetSafeFileName(modelKey);
            ModelCacheResult cleanResult = await ModelCache.GetCleanStepModelWithStatusAsync(
                cleanModeKey,
                () => Task.Run(() =>
                    StepWatermarkCleanVerifier.CleanStepModelFastWithReport(
                        originalStepData,
                        cleanText).CleanStep,
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

            return cleanStepData;
        }

        private F3DPreviewCameraSnapshot CaptureF3DPreviewCameraSnapshot()
        {
            F3DProjectionRenderer.F3DPreviewSession session = _f3dPreviewSession;
            if (session == null)
                return _f3dPreviewCameraSnapshot?.Clone();

            try
            {
                _f3dPreviewCameraSnapshot = session.GetCameraSnapshot(DrainF3DPreviewInteractions());
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to capture F3D preview camera: " + ex);
            }

            return _f3dPreviewCameraSnapshot?.Clone();
        }

        private void StopF3DPreview()
        {
            _f3dPreviewRenderCts?.Cancel();
            _f3dPreviewRenderCts?.Dispose();
            _f3dPreviewRenderCts = null;
            _f3dPreviewSession?.Dispose();
            _f3dPreviewSession = null;
            _f3dPreviewRenderScheduled = false;
            _f3dPreviewRenderRunning = false;
            _f3dPreviewRenderAgain = false;
            _f3dPreviewDragStart = null;
            _f3dPreviewDragButton = null;
            lock (_f3dPreviewInteractionLock)
                _f3dPendingInteractions.Clear();
            _f3dPreviewRenderRequestId++;

            if (f3dModelImage != null)
                f3dModelImage.Source = null;
        }

        private void QueueF3DPreviewRender(F3DPreviewInteraction interaction = null)
        {
            F3DProjectionRenderer.F3DPreviewSession session = _f3dPreviewSession;
            if (session == null || f3dModelImage == null)
                return;

            if (interaction != null)
                CoalesceF3DPreviewInteraction(interaction);

            if (_f3dPreviewRenderScheduled)
                return;

            if (_f3dPreviewRenderRunning)
            {
                _f3dPreviewRenderAgain = true;
                return;
            }

            ScheduleF3DPreviewRender();
        }

        private void CoalesceF3DPreviewInteraction(F3DPreviewInteraction interaction)
        {
            lock (_f3dPreviewInteractionLock)
            {
                if (interaction.Kind == F3DPreviewInteractionKind.MousePosition)
                {
                    _f3dPendingInteractions.RemoveAll(
                        pending => pending.Kind == F3DPreviewInteractionKind.MousePosition);
                }

                _f3dPendingInteractions.Add(interaction);
            }
        }

        private void ScheduleF3DPreviewRender()
        {
            _f3dPreviewRenderCts ??= new CancellationTokenSource();
            CancellationToken renderToken = _f3dPreviewRenderCts.Token;
            int requestId = ++_f3dPreviewRenderRequestId;
            F3DProjectionRenderer.F3DPreviewSession scheduledSession = _f3dPreviewSession;
            _f3dPreviewRenderScheduled = true;

            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(1, renderToken).ConfigureAwait(true);
                    _f3dPreviewRenderScheduled = false;
                    _f3dPreviewRenderRunning = true;
                    await RenderInteractivePreview(requestId, renderToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace("F3D preview render failed: " + ex);
                }
                finally
                {
                    _f3dPreviewRenderScheduled = false;
                    _f3dPreviewRenderRunning = false;
                    if (!renderToken.IsCancellationRequested &&
                        ReferenceEquals(scheduledSession, _f3dPreviewSession) &&
                        (_f3dPreviewRenderAgain || HasPendingF3DPreviewInteractions()))
                    {
                        _f3dPreviewRenderAgain = false;
                        ScheduleF3DPreviewRender();
                    }
                }
            }, DispatcherPriority.Background);
        }

        private async Task RenderInteractivePreview(int requestId, CancellationToken cancellationToken)
        {
            F3DProjectionRenderer.F3DPreviewSession session = _f3dPreviewSession;
            if (session == null)
                return;

            bool isInteractive = _f3dPreviewDragStart != null || HasPendingF3DPreviewInteractions();
            GetF3DPreviewRenderSize(isInteractive, out int width, out int height);
            List<F3DPreviewInteraction> interactions = DrainF3DPreviewInteractions();

            F3DRenderedImage renderedImage = await Task.Run(
                () => session.RenderInteractivePreviewImage(width, height, null, interactions),
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            if (requestId != _f3dPreviewRenderRequestId || !ReferenceEquals(session, _f3dPreviewSession))
                return;

            f3dModelImage.Source = CreateF3DBitmapSource(renderedImage);
            if (isInteractive && _f3dPreviewDragStart == null && !HasPendingF3DPreviewInteractions())
                RequestF3DPreviewIdleRender();
        }

        private void RequestF3DPreviewIdleRender()
        {
            _f3dPreviewRenderAgain = true;
        }

        private List<F3DPreviewInteraction> DrainF3DPreviewInteractions()
        {
            lock (_f3dPreviewInteractionLock)
            {
                if (_f3dPendingInteractions.Count == 0)
                    return null;

                var interactions = new List<F3DPreviewInteraction>(_f3dPendingInteractions);
                _f3dPendingInteractions.Clear();
                return interactions;
            }
        }

        private bool HasPendingF3DPreviewInteractions()
        {
            lock (_f3dPreviewInteractionLock)
                return _f3dPendingInteractions.Count > 0;
        }

        private void GetF3DPreviewRenderSize(bool isInteractive, out int width, out int height)
        {
            double actualWidth = f3dModelViewport?.ActualWidth > 0.0
                ? f3dModelViewport.ActualWidth
                : f3dModelImage.ActualWidth;
            double actualHeight = f3dModelViewport?.ActualHeight > 0.0
                ? f3dModelViewport.ActualHeight
                : f3dModelImage.ActualHeight;
            width = Math.Max(160, (int)Math.Round(actualWidth));
            height = Math.Max(120, (int)Math.Round(actualHeight));

            int maxEdge = isInteractive ? 960 : 1920;
            double scale = Math.Min(1.0, maxEdge / Math.Max(1.0, Math.Max(width, height)));
            if (scale < 1.0)
            {
                width = Math.Max(160, (int)Math.Round(width * scale));
                height = Math.Max(120, (int)Math.Round(height * scale));
            }
        }

        private F3DPreviewInteraction CreateF3DPreviewPointerInteraction(
            Image image,
            Point point,
            F3DPreviewInteractionKind kind,
            MouseButton button = MouseButton.Left)
        {
            bool usesInteractiveFrame = _f3dPreviewDragStart != null ||
                kind == F3DPreviewInteractionKind.MouseWheel;
            GetF3DPreviewRenderSize(usesInteractiveFrame, out int width, out int height);
            double imageWidth = Math.Max(1.0, image.ActualWidth);
            double imageHeight = Math.Max(1.0, image.ActualHeight);
            return new F3DPreviewInteraction
            {
                Kind = kind,
                X = Clamp(point.X / imageWidth, 0.0, 1.0) * width,
                Y = Clamp(point.Y / imageHeight, 0.0, 1.0) * height,
                Button = ConvertF3DPreviewMouseButton(button),
                Modifier = GetF3DPreviewInputModifier()
            };
        }

        private static F3DPreviewMouseButton ConvertF3DPreviewMouseButton(MouseButton button)
        {
            if (button == MouseButton.Right)
                return F3DPreviewMouseButton.Right;
            if (button == MouseButton.Middle)
                return F3DPreviewMouseButton.Middle;
            return F3DPreviewMouseButton.Left;
        }

        private static F3DPreviewInputModifier GetF3DPreviewInputModifier()
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            bool control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (control && shift)
                return F3DPreviewInputModifier.ControlShift;
            if (control)
                return F3DPreviewInputModifier.Control;
            if (shift)
                return F3DPreviewInputModifier.Shift;
            return F3DPreviewInputModifier.None;
        }

        private void F3DPreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged || e.HeightChanged)
                QueueF3DPreviewRender();
        }

        private void F3DPreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_f3dPreviewSession == null || !(sender is Image image))
                return;

            if (e.ClickCount >= 2)
            {
                QueueF3DPreviewRender(new F3DPreviewInteraction
                {
                    Kind = F3DPreviewInteractionKind.ResetCamera,
                    Modifier = GetF3DPreviewInputModifier()
                });
                e.Handled = true;
                return;
            }

            Point position = e.GetPosition(image);
            _f3dPreviewDragStart = position;
            _f3dPreviewDragButton = e.ChangedButton;
            image.CaptureMouse();
            QueueF3DPreviewRender(CreateF3DPreviewPointerInteraction(
                image,
                position,
                F3DPreviewInteractionKind.MouseButtonPress,
                e.ChangedButton));
            e.Handled = true;
        }

        private void F3DPreviewImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_f3dPreviewSession == null ||
                _f3dPreviewDragStart == null ||
                _f3dPreviewDragButton == null ||
                !(sender is Image image))
            {
                return;
            }

            Point current = e.GetPosition(image);
            QueueF3DPreviewRender(CreateF3DPreviewPointerInteraction(
                image,
                current,
                F3DPreviewInteractionKind.MousePosition,
                _f3dPreviewDragButton.Value));
            e.Handled = true;
        }

        private void F3DPreviewImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Image image))
                return;

            Point position = e.GetPosition(image);
            QueueF3DPreviewRender(CreateF3DPreviewPointerInteraction(
                image,
                position,
                F3DPreviewInteractionKind.MouseButtonRelease,
                e.ChangedButton));
            image.ReleaseMouseCapture();
            _f3dPreviewDragStart = null;
            _f3dPreviewDragButton = null;
            e.Handled = true;
        }

        private void F3DPreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_f3dPreviewSession == null)
                return;

            if (sender is Image image)
            {
                Point position = e.GetPosition(image);
                F3DPreviewInteraction interaction = CreateF3DPreviewPointerInteraction(
                    image,
                    position,
                    F3DPreviewInteractionKind.MouseWheel);
                interaction.WheelDirection = e.Delta >= 0
                    ? F3DPreviewWheelDirection.Forward
                    : F3DPreviewWheelDirection.Backward;
                QueueF3DPreviewRender(interaction);
            }
            e.Handled = true;
        }

        private static BitmapSource CreateF3DBitmapSource(F3DRenderedImage image)
        {
            if (image == null ||
                image.Width <= 0 ||
                image.Height <= 0 ||
                image.ChannelType != 0 ||
                image.ChannelTypeSize != 1 ||
                image.RawBytes == null ||
                image.RawBytes.Length == 0)
            {
                return null;
            }

            byte[] bgra = ConvertRawF3DImageToBgra(image.RawBytes, image.Width, image.Height, image.ChannelCount);
            BitmapSource bitmap = BitmapSource.Create(
                image.Width,
                image.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgra,
                image.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static byte[] ConvertRawF3DImageToBgra(byte[] rawBytes, int width, int height, int channelCount)
        {
            if (rawBytes == null)
                throw new ArgumentNullException(nameof(rawBytes));
            if (width <= 0 || height <= 0 || channelCount <= 0)
                throw new InvalidDataException("F3D raw image shape is invalid.");

            int pixelCount = checked(width * height);
            if (rawBytes.Length < pixelCount * channelCount)
                throw new InvalidDataException("F3D raw image data is incomplete.");

            var bgra = new byte[pixelCount * 4];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = (height - 1 - y) * width * channelCount;
                int targetRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int source = sourceRow + x * channelCount;
                    int target = targetRow + x * 4;
                    byte red;
                    byte green;
                    byte blue;
                    byte alpha;
                    if (channelCount == 1)
                    {
                        red = rawBytes[source];
                        green = red;
                        blue = red;
                        alpha = 255;
                    }
                    else
                    {
                        red = rawBytes[source];
                        green = rawBytes[source + 1];
                        blue = rawBytes[source + 2];
                        alpha = channelCount >= 4 ? rawBytes[source + 3] : (byte)255;
                    }

                    bgra[target] = blue;
                    bgra[target + 1] = green;
                    bgra[target + 2] = red;
                    bgra[target + 3] = alpha;
                }
            }

            return bgra;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
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
            bool selectedPreviewIsCurrent = IsCurrentPreviewForSelectedComponent(out _);
            bool hasModel = _currentModel != null && selectedPreviewIsCurrent && !_isCriticalOperationActive;
            bool hasSelectedComponentCache = selectedPreviewIsCurrent && !_isCriticalOperationActive;
            if (saveModelButton != null)
                saveModelButton.IsEnabled = hasModel;
            if (regenerateCleanStepButton != null)
                regenerateCleanStepButton.IsEnabled = hasModel && RemoveWatermark;
            if (removeCacheButton != null)
                removeCacheButton.IsEnabled = hasSelectedComponentCache;
        }

        private bool IsCurrentPreviewForSelectedComponent(out PartInfoViewModel partViewModel)
        {
            partViewModel = resultsGrid?.SelectedItem as PartInfoViewModel;
            string selectedPartNumber = partViewModel?.PartInfo?.Part;
            return !string.IsNullOrWhiteSpace(selectedPartNumber) &&
                string.Equals(selectedPartNumber, _currentPreviewPartNumber, StringComparison.OrdinalIgnoreCase);
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

                var results = await ModelCache.GetSearchProductInfoAsync(Api, partNumber, cts.Token);
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

                var root = await ModelCache.GetComponentJsonAsync(Api, partInfo.Part, cts.Token);

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
                if (removeCacheButton != null)
                    removeCacheButton.IsEnabled = false;
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
            if (_currentModel == null || !RemoveWatermark || !IsCurrentPreviewForSelectedComponent(out PartInfoViewModel partViewModel))
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetImportControlsEnabled(false);
                BeginCriticalOperation("Regenerating selected component cleaned STEP cache...");

                string selectedComponentCacheKey = GetSelectedComponentCacheKey(partViewModel, _currentModel);
                int deletedCount = ModelCache.DeleteCleanStepModels(selectedComponentCacheKey);
                ReportImportProgress(new ImportProgressEvent
                {
                    Message = "Deleted " + deletedCount.ToString(CultureInfo.InvariantCulture) + " cleaned STEP cache file(s).",
                    Percent = 20,
                    IsIndeterminate = false
                });

                previewCts?.Cancel();
                previewCts?.Dispose();
                previewCts = new CancellationTokenSource();

                await LoadPreviewAsync(partViewModel, previewCts.Token);

                CompleteCriticalOperation("Selected component cleaned STEP cache regenerated.", true);
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

        private async void RemoveCacheButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsCurrentPreviewForSelectedComponent(out PartInfoViewModel partViewModel))
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetImportControlsEnabled(false);
                BeginCriticalOperation("Removing selected component cache...");

                string selectedComponentCacheKey = GetSelectedComponentCacheKey(partViewModel, _currentModel);
                string modelUuid = _currentModel?.Uuid;
                int deletedCount = ModelCache.DeleteSelectedComponentCache(
                    partViewModel.PartInfo.Part,
                    selectedComponentCacheKey,
                    modelUuid);
                ReportImportProgress(new ImportProgressEvent
                {
                    Message = "Deleted " + deletedCount.ToString(CultureInfo.InvariantCulture) + " cache file(s).",
                    Percent = 35,
                    IsIndeterminate = false
                });

                previewCts?.Cancel();
                previewCts?.Dispose();
                previewCts = new CancellationTokenSource();

                await LoadPreviewAsync(partViewModel, previewCts.Token);

                CompleteCriticalOperation("Selected component cache removed.", true);
            }
            catch (Exception ex)
            {
                CompleteCriticalOperation($"Failed to remove selected component cache: {ex.Message}", false);
                MessageBox.Show($"Failed to remove selected component cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                SetImportControlsEnabled(true);
                UpdateModelActionButtonState();
            }
        }

        private static string GetSelectedComponentCacheKey(PartInfoViewModel partViewModel, EeFootprint3dModel modelInfo)
        {
            string partNumber = FirstNonEmpty(partViewModel?.PartInfo?.Part, partViewModel?.PartInfo?.Name);
            string modelKey = FirstNonEmpty(modelInfo?.Uuid, modelInfo?.Name);
            return ModelCache.GetComponentModelCacheKey(partNumber, modelKey);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
                return string.Empty;

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
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
            string selectedComponentCacheKey = GetSelectedComponentCacheKey(resultsGrid?.SelectedItem as PartInfoViewModel, modelInfo);
            string cleanModeKey = CleanStepCacheKeys.GetCleanModeKey(selectedComponentCacheKey, CleanText);
            return await ModelCache.GetCleanStepModelAsync(
                cleanModeKey,
                () => Task.Run(() =>
                    StepWatermarkCleanVerifier.CleanOrThrow(
                        originalModel,
                        ModelCache.GetSafeFileName(selectedComponentCacheKey),
                        Path.Combine(
                            ModelCache.GetLocalDataRoot(),
                            "StepCleanerReports",
                            ModelCache.GetSafeFileName(selectedComponentCacheKey) +
                            (CleanText ? "_text" : string.Empty) +
                            "_" +
                            DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)),
                        CleanText),
                    cancellationToken),
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
