using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;

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
        private readonly AxisAngleRotation3D _objPreviewRotationX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -65);
        private readonly AxisAngleRotation3D _objPreviewRotationY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), -20);
        private readonly ScaleTransform3D _objPreviewScale = new ScaleTransform3D(1, 1, 1);
        private readonly TranslateTransform3D _objPreviewTranslate = new TranslateTransform3D();
        private readonly Transform3DGroup _objPreviewTransform = new Transform3DGroup();
        private Point _objPreviewDragStart;
        private double _objPreviewDragStartRotationX;
        private double _objPreviewDragStartRotationY;
        private double _objPreviewDragStartTranslateX;
        private double _objPreviewDragStartTranslateY;
        private bool _isObjPreviewLeftDragging;
        private bool _isObjPreviewRightDragging;
        private static readonly char[] PartNumberSeparators = { '\r', '\n', '\t', ' ', ',', ';', '|' };
        private static readonly string SessionStateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyEDA-Loader",
            "dialog-session.json");

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
            _objPreviewTransform.Children.Add(_objPreviewScale);
            _objPreviewTransform.Children.Add(new RotateTransform3D(_objPreviewRotationX));
            _objPreviewTransform.Children.Add(new RotateTransform3D(_objPreviewRotationY));
            _objPreviewTransform.Children.Add(_objPreviewTranslate);

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
                                UpdatePreviewProgress("Loading OBJ preview...", 78);
                                await ShowObjModelPreviewAsync(_currentModel, cancellationToken);
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
            objModelPreviewVisual.Content = null;
            ResetObjPreviewTransform();
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

        private async Task ShowObjModelPreviewAsync(EeFootprint3dModel modelInfo, CancellationToken cancellationToken)
        {
            objModelPreviewVisual.Content = null;
            ResetObjPreviewTransform();

            if (modelInfo == null)
                return;

            byte[] objData = await ModelCache.GetRawObjModelAsync(Api, modelInfo.Uuid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (objData == null || objData.Length == 0)
                return;

            Model3DGroup model = BuildObjPreviewModel(objData);
            cancellationToken.ThrowIfCancellationRequested();

            if (model == null)
                return;

            model.Transform = _objPreviewTransform;
            objModelPreviewVisual.Content = model;
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

        private static Model3DGroup BuildObjPreviewModel(byte[] objData)
        {
            var vertices = new List<Point3D>();
            var indices = new List<int>();

            using (var reader = new StringReader(System.Text.Encoding.UTF8.GetString(objData)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                        continue;

                    if (string.Equals(parts[0], "v", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                    {
                        if (double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                            double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y) &&
                            double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z))
                            vertices.Add(new Point3D(x, y, z));
                    }
                    else if (string.Equals(parts[0], "f", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                    {
                        var face = new List<int>();
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (TryParseObjVertexIndex(parts[i], vertices.Count, out int index))
                                face.Add(index);
                        }

                        for (int i = 1; i + 1 < face.Count; i++)
                        {
                            indices.Add(face[0]);
                            indices.Add(face[i]);
                            indices.Add(face[i + 1]);
                        }
                    }
                }
            }

            if (vertices.Count == 0 || indices.Count == 0)
                return null;

            Rect3D bounds = CalculateBounds(vertices);
            double maxSize = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
            if (maxSize <= 0)
                maxSize = 1;

            var mesh = new MeshGeometry3D();
            double scale = 2.2 / maxSize;
            double centerX = bounds.X + bounds.SizeX / 2.0;
            double centerY = bounds.Y + bounds.SizeY / 2.0;
            double centerZ = bounds.Z + bounds.SizeZ / 2.0;

            foreach (Point3D vertex in vertices)
            {
                mesh.Positions.Add(new Point3D(
                    (vertex.X - centerX) * scale,
                    (vertex.Y - centerY) * scale,
                    (vertex.Z - centerZ) * scale));
            }

            foreach (int index in indices)
                mesh.TriangleIndices.Add(index);

            var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(180, 190, 205)));
            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Color.FromRgb(70, 70, 70)));
            group.Children.Add(new GeometryModel3D(mesh, material)
            {
                BackMaterial = material
            });

            return group;
        }

        private static bool TryParseObjVertexIndex(string token, int vertexCount, out int index)
        {
            index = -1;
            string first = token.Split('/')[0];
            if (!int.TryParse(first, out int objIndex) || objIndex == 0)
                return false;

            index = objIndex > 0 ? objIndex - 1 : vertexCount + objIndex;
            return index >= 0 && index < vertexCount;
        }

        private static Rect3D CalculateBounds(IReadOnlyList<Point3D> vertices)
        {
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            double maxZ = double.NegativeInfinity;

            foreach (Point3D vertex in vertices)
            {
                minX = Math.Min(minX, vertex.X);
                minY = Math.Min(minY, vertex.Y);
                minZ = Math.Min(minZ, vertex.Z);
                maxX = Math.Max(maxX, vertex.X);
                maxY = Math.Max(maxY, vertex.Y);
                maxZ = Math.Max(maxZ, vertex.Z);
            }

            return new Rect3D(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ);
        }

        private void ResetObjPreviewTransform()
        {
            _objPreviewRotationX.Angle = -65;
            _objPreviewRotationY.Angle = -20;
            _objPreviewScale.ScaleX = 1;
            _objPreviewScale.ScaleY = 1;
            _objPreviewScale.ScaleZ = 1;
            _objPreviewTranslate.OffsetX = 0;
            _objPreviewTranslate.OffsetY = 0;
            _objPreviewTranslate.OffsetZ = 0;
            _isObjPreviewLeftDragging = false;
            _isObjPreviewRightDragging = false;
        }

        private void ObjModelViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (objModelPreviewVisual.Content == null)
                return;

            _isObjPreviewLeftDragging = true;
            _isObjPreviewRightDragging = false;
            _objPreviewDragStart = e.GetPosition(objModelViewport);
            _objPreviewDragStartRotationX = _objPreviewRotationX.Angle;
            _objPreviewDragStartRotationY = _objPreviewRotationY.Angle;
            objModelViewport.Focus();
            objModelViewport.CaptureMouse();
            e.Handled = true;
        }

        private void ObjModelViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isObjPreviewLeftDragging)
                return;

            _isObjPreviewLeftDragging = false;
            if (!_isObjPreviewRightDragging)
                objModelViewport.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ObjModelViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (objModelPreviewVisual.Content == null)
                return;

            _isObjPreviewRightDragging = true;
            _isObjPreviewLeftDragging = false;
            _objPreviewDragStart = e.GetPosition(objModelViewport);
            _objPreviewDragStartTranslateX = _objPreviewTranslate.OffsetX;
            _objPreviewDragStartTranslateY = _objPreviewTranslate.OffsetY;
            objModelViewport.Focus();
            objModelViewport.CaptureMouse();
            e.Handled = true;
        }

        private void ObjModelViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isObjPreviewRightDragging)
                return;

            _isObjPreviewRightDragging = false;
            if (!_isObjPreviewLeftDragging)
                objModelViewport.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ObjModelViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (objModelPreviewVisual.Content == null)
                return;

            Point current = e.GetPosition(objModelViewport);
            double dx = current.X - _objPreviewDragStart.X;
            double dy = current.Y - _objPreviewDragStart.Y;

            if (_isObjPreviewLeftDragging)
            {
                _objPreviewRotationY.Angle = _objPreviewDragStartRotationY + dx * 0.6;
                _objPreviewRotationX.Angle = _objPreviewDragStartRotationX + dy * 0.6;
                e.Handled = true;
            }
            else if (_isObjPreviewRightDragging)
            {
                _objPreviewTranslate.OffsetX = _objPreviewDragStartTranslateX + dx / 120.0;
                _objPreviewTranslate.OffsetY = _objPreviewDragStartTranslateY - dy / 120.0;
                e.Handled = true;
            }
        }

        private void ObjModelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (objModelPreviewVisual.Content == null)
                return;

            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double scale = Math.Max(0.15, Math.Min(8.0, _objPreviewScale.ScaleX * factor));
            _objPreviewScale.ScaleX = scale;
            _objPreviewScale.ScaleY = scale;
            _objPreviewScale.ScaleZ = scale;
            e.Handled = true;
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
