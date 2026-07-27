using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EasyEDA_Loader
{
    public partial class LayoutDuplicatorDialog : Window
    {
        private readonly LayoutDuplicationSession session;
        private readonly OllamaLayoutMappingClient ollamaClient;
        private readonly LayoutDuplicatorViewModel viewModel = new LayoutDuplicatorViewModel();
        private CancellationTokenSource cancellation;
        private LayoutSchematicMatchContext schematicMatchContext;
        private bool schematicMatchContextLoaded;
        private bool dialogLoaded;

        internal LayoutDuplicatorDialog(LayoutDuplicationSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            ollamaClient = new OllamaLayoutMappingClient();
            InitializeComponent();
            DataContext = viewModel;
            sourceComponentsGrid.ItemsSource = viewModel.SourceComponents;
            targetAnchorsGrid.ItemsSource = viewModel.TargetAnchors;
            modelComboBox.ItemsSource = viewModel.Models;

            foreach (LayoutComponentSnapshot component in session.SourceComponents)
                viewModel.SourceComponents.Add(LayoutComponentRowViewModel.FromComponent(component));

            if (viewModel.SourceComponents.Count > 0)
                sourceComponentsGrid.SelectedIndex = 0;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            dialogLoaded = true;
            RebuildTargetAnchors();
            cancellation = new CancellationTokenSource();
            await RefreshModelsAsync(cancellation.Token).ConfigureAwait(true);
        }

        private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
        {
            cancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            await RefreshModelsAsync(cancellation.Token).ConfigureAwait(true);
        }

        private void SourceComponentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RebuildTargetAnchors();
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (modelComboBox.SelectedItem is OllamaModelInfo model)
                modelStatusText.Text = model.ToString();
        }

        private void UseSchematicMatchingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            RebuildTargetAnchors();
        }

        private async void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                duplicateButton.IsEnabled = false;
                cancellation = new CancellationTokenSource();
                await DuplicateAsync(cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                SetProgress("Duplicate layout cancelled.", null, false);
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Duplicate layout failed: " + ex);
                MessageBox.Show(ex.Message, "Duplicate layout", MessageBoxButton.OK, MessageBoxImage.Error);
                SetProgress("Duplicate layout failed.", null, false);
            }
            finally
            {
                duplicateButton.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            cancellation?.Cancel();
            DialogResult = false;
        }

        private async Task RefreshModelsAsync(CancellationToken cancellationToken)
        {
            SetProgress("Checking Ollama...", null, true);
            viewModel.Models.Clear();

            IReadOnlyList<string> installed = Array.Empty<string>();
            IReadOnlyList<string> loaded = Array.Empty<string>();
            try
            {
                installed = await ollamaClient.GetInstalledModelsAsync(cancellationToken).ConfigureAwait(true);
                loaded = await ollamaClient.GetLoadedModelsAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                modelStatusText.Text = "Ollama unavailable: " + ex.Message;
            }

            string selected = OllamaLayoutMappingClient.SelectInitialModel(installed, loaded, session.LastUsedModel);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddModel(names, installed, loaded, selected);
            AddModel(names, installed, loaded, LayoutDuplicationDefaults.DefaultModelName);
            AddModel(names, installed, loaded, LayoutDuplicationDefaults.FallbackModelName);
            foreach (string name in loaded)
                AddModel(names, installed, loaded, name);
            foreach (string name in installed)
                AddModel(names, installed, loaded, name);

            OllamaModelInfo selectedModel = viewModel.Models.FirstOrDefault(model => string.Equals(model.Name, selected, StringComparison.OrdinalIgnoreCase))
                ?? viewModel.Models.FirstOrDefault();
            modelComboBox.SelectedItem = selectedModel;
            modelStatusText.Text = selectedModel?.ToString() ?? "No model selected";

            if (selectedModel != null && selectedModel.IsInstalled)
            {
                try
                {
                    SetProgress("Warming model " + selectedModel.Name + "...", null, true);
                    await ollamaClient.WarmModelAsync(selectedModel.Name, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    modelStatusText.Text = "Warm failed: " + ex.Message;
                }
            }

            SetProgress("", 0, false);
        }

        private void AddModel(HashSet<string> names, IReadOnlyList<string> installed, IReadOnlyList<string> loaded, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                return;

            viewModel.Models.Add(new OllamaModelInfo
            {
                Name = name,
                IsInstalled = installed.Any(model => string.Equals(model, name, StringComparison.OrdinalIgnoreCase)),
                IsLoaded = loaded.Any(model => string.Equals(model, name, StringComparison.OrdinalIgnoreCase))
            });
        }

        private void RebuildTargetAnchors()
        {
            viewModel.TargetAnchors.Clear();
            if (!dialogLoaded)
                return;

            if (!(sourceComponentsGrid.SelectedItem is LayoutComponentRowViewModel sourceRow))
                return;

            SetProgress("Finding target components...", null, true);
            LayoutSchematicMatchContext schematicContext = GetSchematicMatchContext();
            foreach (LayoutComponentSnapshot component in LayoutDuplicationCapture.CaptureTargetAnchors(session, sourceRow.Component, schematicContext))
                viewModel.TargetAnchors.Add(LayoutComponentRowViewModel.FromComponent(component));
            SetProgress(viewModel.TargetAnchors.Count == 0 ? "No schematic-matched target components found." : "", 0, false);
        }

        private LayoutSchematicMatchContext GetSchematicMatchContext()
        {
            if (!(useSchematicMatchingCheckBox.IsChecked == true))
                return null;

            if (schematicMatchContextLoaded)
                return schematicMatchContext;

            SetProgress("Reading schematic matching hints...", null, true);
            if (!LayoutDuplicationSchematicMatcher.TryBuildSchematicMatchContext(session, out schematicMatchContext))
                schematicMatchContext = new LayoutSchematicMatchContext();

            schematicMatchContextLoaded = true;
            SetProgress("", 0, false);
            return schematicMatchContext;
        }

        private IReadOnlyList<LayoutSchematicComponentHint> BuildSchematicHints(
            LayoutSchematicMatchContext schematicContext,
            IEnumerable<LayoutComponentSnapshot> checkedTargets,
            IEnumerable<LayoutComponentSnapshot> destinationCandidates)
        {
            if (schematicContext == null || !schematicContext.HasHints)
                return Array.Empty<LayoutSchematicComponentHint>();

            return LayoutDuplicationSchematicMatcher.GetHintsForComponents(
                schematicContext,
                session.SourceComponents
                    .Concat(checkedTargets ?? Enumerable.Empty<LayoutComponentSnapshot>())
                    .Concat(destinationCandidates ?? Enumerable.Empty<LayoutComponentSnapshot>()));
        }

        private async Task DuplicateAsync(CancellationToken cancellationToken)
        {
            if (!(sourceComponentsGrid.SelectedItem is LayoutComponentRowViewModel sourceRow))
                throw new InvalidOperationException("Select a source anchor component.");

            var checkedTargets = viewModel.TargetAnchors
                .Where(row => row.IsChecked)
                .Select(row => row.Component)
                .ToList();
            if (checkedTargets.Count == 0)
                throw new InvalidOperationException("Select at least one target anchor.");

            if (!(modelComboBox.SelectedItem is OllamaModelInfo model))
                throw new InvalidOperationException("Select an Ollama model.");

            if (!model.IsInstalled)
            {
                MessageBoxResult answer = MessageBox.Show(
                    "Ollama model '" + model.Name + "' is not installed. Pull it now?",
                    "Duplicate layout",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                    return;

                await ollamaClient.PullModelAsync(model.Name, new Progress<LayoutDuplicationProgress>(ReportProgress), cancellationToken).ConfigureAwait(true);
            }

            OllamaLayoutMappingClient.SaveLastUsedModel(model.Name);
            bool useSchematicMatching = useSchematicMatchingCheckBox.IsChecked == true;
            LayoutSchematicMatchContext schematicContext = useSchematicMatching ? GetSchematicMatchContext() : null;
            SetProgress("Building mapping prompt...", null, true);
            IReadOnlyList<LayoutComponentSnapshot> destinationCandidates =
                LayoutDuplicationCapture.CaptureDestinationCandidates(session, checkedTargets, schematicContext);
            var request = new LayoutMappingRequest
            {
                SourceAnchor = sourceRow.Component,
                SourceComponents = session.SourceComponents,
                TargetAnchors = checkedTargets,
                DestinationCandidates = destinationCandidates,
                UseSchematicMatching = useSchematicMatching,
                SchematicHints = BuildSchematicHints(schematicContext, checkedTargets, destinationCandidates)
            };

            string prompt = LayoutDuplicationMapper.BuildMappingPrompt(request);
            SetProgress("Waiting for AI mapping...", null, true);
            string response = await ollamaClient.RequestMappingAsync(model.Name, prompt, cancellationToken).ConfigureAwait(true);
            SetProgress("Validating mapping...", null, true);
            LayoutMappingValidationResult validation = LayoutDuplicationMapper.ValidateMappingResponse(response, request);
            if (!validation.HasValidGroups)
                throw new InvalidOperationException("No valid layout mappings were returned: " + string.Join("; ", validation.Errors));

            LayoutDuplicationResult result = LayoutDuplicationApply.ApplyLayoutDuplication(
                session,
                sourceRow.Component,
                validation,
                new Progress<LayoutDuplicationProgress>(ReportProgress));

            SetProgress("Redrawing board...", 100, false);
            string summary = "Placed components: " + result.PlacedComponents + ". Copied routing primitives: " + result.CopiedRoutingPrimitives + ".";
            if (validation.Errors.Count > 0 || result.Warnings.Count > 0)
                summary += Environment.NewLine + string.Join(Environment.NewLine, validation.Errors.Concat(result.Warnings));

            MessageBox.Show(summary, "Duplicate layout", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }

        private void ReportProgress(LayoutDuplicationProgress progress)
        {
            if (progress == null)
                return;

            SetProgress(progress.Message, progress.Percent, progress.IsIndeterminate);
        }

        private void SetProgress(string message, double? percent, bool isIndeterminate)
        {
            operationProgressText.Text = message ?? "";
            operationProgressBar.IsIndeterminate = isIndeterminate;
            operationProgressBar.Value = percent.HasValue ? percent.Value : 0;
        }
    }
}
