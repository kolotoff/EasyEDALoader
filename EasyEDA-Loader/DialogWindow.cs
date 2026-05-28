using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private static readonly char[] PartNumberSeparators = { '\r', '\n', '\t', ' ', ',', ';', '|' };

        public List<ComponentSelection> SelectedComponents { get; private set; }
        public bool CloseDocuments => closeDocumentsCheckBox?.IsChecked == true;
        public bool PlaceInSchematic => placeInSchematicCheckBox?.IsChecked == true;

        public DialogWindow()
        {
            InitializeComponent();
            
            Api = new EasyedaApi();
            cts = new CancellationTokenSource();
            previewCts = new CancellationTokenSource();
            searchResults = new ObservableCollection<PartInfoViewModel>();
            SelectedComponents = new List<ComponentSelection>();
            
            resultsGrid.ItemsSource = searchResults;

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
            searchTextBox.Focus();
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
            _currentComponent = null;
            _currentModel = null;
            _currentRoot = null;
            saveModelButton.IsEnabled = false;
            SetPreviewProgress(false);
        }

        public void UpdateAddButtonState()
        {
            addToLibraryButton.IsEnabled = searchResults.Any(p => p.AddToLibrary);
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
                        IncludeFootprint = hasFootprint
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
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var partNumbers = GetPartNumbersOrShowMessage();
            if (partNumbers.Count == 0)
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

                    var modelData = await Task.Run(() => Api.LoadModelAsync(_currentModel.Uuid, cts.Token));

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

        protected override void OnClosing(CancelEventArgs e)
        {
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
}
