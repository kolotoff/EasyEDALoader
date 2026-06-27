using DXP;
using PCB;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class EasyEDALoaderModule : ServerModule
    {
        private bool noGUIMode;
        private readonly EasyEdaCommandBridge commandBridge;
        private readonly SynchronizationContext bridgeSynchronizationContext;
        private int loaderDialogOpen;
        private static readonly List<CommandProc> registeredCommandProcs = new List<CommandProc>();

        public EasyEDALoaderModule(IClient argClient)
          : base(argClient, "EasyEDA-Loader")
        {
            noGUIMode = argClient.ProductInfo().SupportsUIFeature("NoGUI", false);
            bridgeSynchronizationContext = SynchronizationContext.Current;
            commandBridge = new EasyEdaCommandBridge();
            commandBridge.CommandReceived += HandleBridgeCommand;
            commandBridge.Start();
            Trace("Module constructed.");
        }

        protected override IServerDocument NewDocumentInstance(string argKind, string argFileName) => (IServerDocument)null;

        protected override void InitializeCommands()
        {
            Trace("InitializeCommands.");
            RegisterCommand("EasyEDARun", new CommandProc(Run));
            RegisterCommand("EasyEDA-Loader:EasyEDARun", new CommandProc(Run));
            RegisterCommand("EasyEDAReproject3D", new CommandProc(ReprojectActiveFootprint3D));
            RegisterCommand("EasyEDA-Loader:EasyEDAReproject3D", new CommandProc(ReprojectActiveFootprint3D));
            RegisterCommand("EasyEDAAlign3DModel", new CommandProc(AlignActiveFootprint3DModel));
            RegisterCommand("EasyEDA-Loader:EasyEDAAlign3DModel", new CommandProc(AlignActiveFootprint3DModel));
            RegisterCommand("EasyEDASwitchTopSignalLayer", new CommandProc(SwitchTopSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchTopSignalLayer", new CommandProc(SwitchTopSignalLayer));
            RegisterCommand("EasyEDASwitchBottomSignalLayer", new CommandProc(SwitchBottomSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchBottomSignalLayer", new CommandProc(SwitchBottomSignalLayer));
            RegisterCommand("EasyEDASwitchNextSignalLayer", new CommandProc(SwitchNextSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchNextSignalLayer", new CommandProc(SwitchNextSignalLayer));
            RegisterCommand("EasyEDASwitchPreviousSignalLayer", new CommandProc(SwitchPreviousSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchPreviousSignalLayer", new CommandProc(SwitchPreviousSignalLayer));
        }

        private void RegisterCommand(string argCommandId, CommandProc commandProc)
        {
            CommandProc wrappedProc = (IServerDocumentView view, ref string parameters) =>
            {
                try
                {
                    Trace($"Command invoked: {argCommandId}");
                    commandProc(view, ref parameters);
                }
                catch (Exception ex)
                {
                    Trace($"Command failed: {ex}");
                    if (noGUIMode)
                    {
                        throw;
                    }
                    else
                    {
                        int num = (int)MessageBox.Show(ex.Message, "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
            };

            registeredCommandProcs.Add(wrappedProc);
            ((DXP.CommandLauncher)CommandLauncher).RegisterCommand(argCommandId, wrappedProc, null);
            Trace($"Registered command: {argCommandId}");
        }

        internal static void Trace(string message)
        {
            string line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            foreach (string logPath in GetTracePaths())
            {
                try
                {
                    File.AppendAllText(logPath, line);
                    return;
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<string> GetTracePaths()
        {
            string assemblyLocation = typeof(EasyEDALoaderModule).Assembly.Location;
            if (!string.IsNullOrEmpty(assemblyLocation))
                yield return Path.Combine(Path.GetDirectoryName(assemblyLocation), "EasyEDA-Loader.log");

            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApplicationData))
                yield return Path.Combine(localApplicationData, "Altium", "EasyEDA-Loader.log");

            yield return Path.Combine(Path.GetTempPath(), "EasyEDA-Loader.log");
        }

        private static EESCH.SchematicPropertySet BuildSchematicPropertySet(
            SymbolData symbolData,
            FootprintData footprintData,
            EasyedaApi.ProductInfo productInfo,
            string designator,
            string partName,
            string description,
            string footprintName,
            string package,
            string pcbLibraryPath,
            string mounting)
        {
            SymbolParameters symbolParameters = symbolData?.Head?.Parameters;
            string manufacturer = FirstNonEmpty(
                symbolParameters?.Manufacturer,
                GetProductParameter(productInfo, "Manufacturer"),
                GetProductParameter(productInfo, "MFR"),
                GetProductParameter(productInfo, "Mfr."));

            return new EESCH.SchematicPropertySet
            {
                Manufacturer = manufacturer,
                ValueType = EESCH.SelectRuleValueType(designator, partName, description, package),
                Footprint = CleanPropertyValue(footprintName),
                FootprintLibrary = CleanPropertyValue(pcbLibraryPath),
                Package = "",
                Mounting = ""
            };
        }

        private static string SelectPartNumber(ComponentSelection selection, ComponentInfo component, SymbolData symbolData, EasyedaApi.ProductInfo productInfo)
        {
            return SymbolImportRules.SelectDesignItemId(
                manufacturerPart: FirstNonEmpty(
                    symbolData?.Head?.Parameters?.ManufacturerPart,
                    GetProductParameter(productInfo, "Manufacturer Part"),
                    GetProductParameter(productInfo, "ManufacturerPart"),
                    GetProductParameter(productInfo, "MPN"),
                    GetProductParameter(productInfo, "Mfr. Part")),
                symbolName: symbolData?.Head?.Parameters?.Name,
                componentTitle: component?.Title,
                searchResultName: selection?.PartInfo?.Name,
                searchPart: selection?.PartInfo?.Part,
                lcscNumber: component?.Lcsc?.Number,
                szlcscNumber: component?.Szlcsc?.Number);
        }

        private static string SelectFootprintDescription(ComponentInfo component, EasyedaApi.ProductInfo productInfo, string partNumber, string package, string mounting)
        {
            return FootprintMetadataSelector.SelectDescription(
                productInfo?.Description,
                component?.Description,
                component?.PackageDetail?.Title,
                package,
                partNumber,
                mounting,
                productInfo?.Parameters,
                BuildFootprintDescriptionGeometry(component?.PackageDetail?.Footprint));
        }

        private static double SelectFootprintHeightMm(EasyedaApi.ProductInfo productInfo)
        {
            double height = productInfo?.Size?.Z ?? 0;
            return height > 0 ? height : 0;
        }

        private static FootprintDescriptionGeometry BuildFootprintDescriptionGeometry(FootprintData footprintData)
        {
            if (footprintData == null)
                return null;

            var padCenters = new List<Tuple<double, double>>();
            if (footprintData.Shapes != null)
            {
                foreach (var shape in footprintData.Shapes)
                {
                    if (shape is EeFootprintPad pad && IsNumberedElectricalPad(pad))
                        padCenters.Add(Tuple.Create(pad.CenterX, pad.CenterY));
                }
            }

            return new FootprintDescriptionGeometry
            {
                PositionCount = padCenters.Count,
                PitchMm = EstimatePitchMm(padCenters),
                BodyWidthMm = footprintData.BoundingBox?.Width ?? 0,
                BodyHeightMm = footprintData.BoundingBox?.Height ?? 0
            };
        }

        private static bool IsNumberedElectricalPad(EeFootprintPad pad)
        {
            if (pad == null || string.IsNullOrWhiteSpace(pad.Number))
                return false;

            return int.TryParse(pad.Number.Trim(), out _);
        }

        private static double EstimatePitchMm(List<Tuple<double, double>> padCenters)
        {
            if (padCenters == null || padCenters.Count < 2)
                return 0;

            double minX = padCenters[0].Item1;
            double maxX = padCenters[0].Item1;
            double minY = padCenters[0].Item2;
            double maxY = padCenters[0].Item2;
            foreach (var center in padCenters)
            {
                minX = Math.Min(minX, center.Item1);
                maxX = Math.Max(maxX, center.Item1);
                minY = Math.Min(minY, center.Item2);
                maxY = Math.Max(maxY, center.Item2);
            }

            bool useX = (maxX - minX) >= (maxY - minY);
            var coordinates = new List<double>();
            foreach (var center in padCenters)
                coordinates.Add(useX ? center.Item1 : center.Item2);

            coordinates.Sort();
            double pitch = 0;
            for (int i = 1; i < coordinates.Count; i++)
            {
                double delta = Math.Abs(coordinates[i] - coordinates[i - 1]);
                if (delta <= 0.001)
                    continue;

                if (pitch <= 0 || delta < pitch)
                    pitch = delta;
            }

            return pitch;
        }

        private static string InferMounting(FootprintData footprintData)
        {
            if (footprintData?.Shapes == null || footprintData.Layers == null)
                return "";

            bool hasSmd = false;
            bool hasThroughHole = false;
            foreach (var shape in footprintData.Shapes)
            {
                if (shape is EeFootprintPad pad)
                {
                    string layerName = footprintData.Layers.GetLayer(pad.Layer)?.Name;
                    if (string.Equals(layerName, "TopLayer", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layerName, "BottomLayer", StringComparison.OrdinalIgnoreCase))
                        hasSmd = true;
                    else if (string.Equals(layerName, "Multi-Layer", StringComparison.OrdinalIgnoreCase))
                        hasThroughHole = true;
                }
                else if (shape is EeFootprintHole)
                {
                    hasThroughHole = true;
                }
            }

            if (hasSmd && hasThroughHole)
                return "hybrid";
            if (hasSmd)
                return "SMT";
            if (hasThroughHole)
                return "through-hole";

            return "";
        }

        private static string GetProductParameter(EasyedaApi.ProductInfo productInfo, string parameterName)
        {
            if (productInfo?.Parameters == null)
                return "";

            foreach (var kvp in productInfo.Parameters)
            {
                if (string.Equals(kvp.Key, parameterName, StringComparison.OrdinalIgnoreCase))
                    return CleanPropertyValue(kvp.Value);
            }

            return "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                string cleaned = CleanPropertyValue(value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    return cleaned;
            }

            return "";
        }

        private static string CleanPropertyValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-" || value.Trim() == "*")
                return "";

            return value.Trim();
        }

        private void Run(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("Run entered.");
            if (IsLoaderDialogOpen())
            {
                Trace("Run ignored because dialog is already open.");
                return;
            }

            Interlocked.Exchange(ref loaderDialogOpen, 1);
            try
            {
                Dialog dialog = new Dialog((selections, progress) => ImportSelectedComponents(selections, progress));
                DialogResult result = dialog.ShowDialog();
                Trace("Dialog closed with result: " + result);
            }
            finally
            {
                Interlocked.Exchange(ref loaderDialogOpen, 0);
            }
        }

        private void ReprojectActiveFootprint3D(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("ReprojectActiveFootprint3D entered.");
            IPCB_Group component = GetActivePcbLibComponentOrThrow();
            IServerDocument document = GetCurrentDocument();
            int removedCount = 0;
            int projectionCount = 0;
            BeginPcbLibraryEdit(document);
            AltiumApi.GlobalVars.PCBServer.PreProcess();
            try
            {
                projectionCount = EEPCB.ReprojectComponentBodySilhouette(component, out removedCount);
            }
            finally
            {
                AltiumApi.GlobalVars.PCBServer.PostProcess();
                EndPcbLibraryEdit(document);
            }

            MarkCurrentDocumentModified();
            EEPCB.GetPcbGroupBoard(component)?.ViewManager_FullUpdate();
            Trace($"ReprojectActiveFootprint3D completed. Removed={removedCount} Projection={projectionCount}");
            ShowInfo($"Reprojected 3D silhouette: removed {removedCount}, added {projectionCount} projection primitive(s).");
        }

        private void AlignActiveFootprint3DModel(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("AlignActiveFootprint3DModel entered.");
            IPCB_Group component = GetActivePcbLibComponentOrThrow();
            IServerDocument document = GetCurrentDocument();
            int alignedCount = 0;
            BeginPcbLibraryEdit(document);
            AltiumApi.GlobalVars.PCBServer.PreProcess();
            try
            {
                alignedCount = EEPCB.AlignComponentBodiesToPads(component);
            }
            finally
            {
                AltiumApi.GlobalVars.PCBServer.PostProcess();
                EndPcbLibraryEdit(document);
            }

            MarkCurrentDocumentModified();
            EEPCB.GetPcbGroupBoard(component)?.ViewManager_FullUpdate();
            Trace($"AlignActiveFootprint3DModel completed. Aligned={alignedCount}");
            ShowInfo($"Aligned {alignedCount} 3D model body/bodies to pad bounds.");
        }

        private void SwitchTopSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchTopSignalLayer entered.");
            if (!EEPCB.SwitchToTopSignalLayer())
                throw new InvalidOperationException("Could not switch to the top signal layer. Open a PCB document and try again.");
        }

        private void SwitchBottomSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchBottomSignalLayer entered.");
            if (!EEPCB.SwitchToBottomSignalLayer())
                throw new InvalidOperationException("Could not switch to the bottom signal layer. Open a PCB document and try again.");
        }

        private void SwitchNextSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchNextSignalLayer entered.");
            if (!EEPCB.SwitchToNextSignalLayer())
                throw new InvalidOperationException("Could not switch to the next displayed signal layer. Open a PCB document with displayed signal layers and try again.");
        }

        private void SwitchPreviousSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchPreviousSignalLayer entered.");
            if (!EEPCB.SwitchToPreviousSignalLayer())
                throw new InvalidOperationException("Could not switch to the previous displayed signal layer. Open a PCB document with displayed signal layers and try again.");
        }

        private EasyEdaCommandBridge.CommandResponse HandleBridgeCommand(string command)
        {
            if (IsLoaderDialogOpen())
            {
                return EasyEdaCommandBridge.CommandResponse.Error(
                    "loader-dialog-open",
                    "EasyEDALoader window is open. Close it before running Ulanzi commands.",
                    command);
            }

            if (bridgeSynchronizationContext != null &&
                SynchronizationContext.Current != bridgeSynchronizationContext)
            {
                EasyEdaCommandBridge.CommandResponse response = null;
                Exception exception = null;
                using (var completed = new ManualResetEventSlim(false))
                {
                    bridgeSynchronizationContext.Post(
                        _ =>
                        {
                            try
                            {
                                response = HandleBridgeCommandOnAltiumThread(command);
                            }
                            catch (Exception ex)
                            {
                                exception = ex;
                            }
                            finally
                            {
                                completed.Set();
                            }
                        },
                        null);

                    if (!completed.Wait(TimeSpan.FromSeconds(30)))
                    {
                        return EasyEdaCommandBridge.CommandResponse.Error(
                            "command-timeout",
                            "Timed out waiting for Altium to execute EasyEDALoader command.",
                            command);
                    }
                }

                if (exception != null)
                    throw exception;

                return response;
            }

            return HandleBridgeCommandOnAltiumThread(command);
        }

        private bool IsLoaderDialogOpen()
        {
            return Interlocked.CompareExchange(ref loaderDialogOpen, 0, 0) != 0;
        }

        private EasyEdaCommandBridge.CommandResponse HandleBridgeCommandOnAltiumThread(string command)
        {
            string parameters = string.Empty;
            IServerDocumentView context = null;

            switch (command)
            {
                case EasyEdaCommandBridge.CommandOpenLoader:
                    Run(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandReproject3D:
                    ReprojectActiveFootprint3D(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandAlign3DModel:
                    AlignActiveFootprint3DModel(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerTop:
                    SwitchTopSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerBottom:
                    SwitchBottomSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerNext:
                    SwitchNextSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerPrevious:
                    SwitchPreviousSignalLayer(context, ref parameters);
                    break;
                default:
                    return EasyEdaCommandBridge.CommandResponse.Error(
                        "invalid-command",
                        "Unknown EasyEDALoader bridge command.",
                        command);
            }

            return EasyEdaCommandBridge.CommandResponse.Ok(command);
        }

        private static IPCB_Group GetActivePcbLibComponentOrThrow()
        {
            IPCB_Group component = EEPCB.GetCurrentPcbLibComponent();
            if (component == null)
                throw new InvalidOperationException("Open a PCB library and select a footprint before running this command.");

            return component;
        }

        private void MarkCurrentDocumentModified()
        {
            try
            {
                GetCurrentDocument()?.SetModified(true);
            }
            catch
            {
            }
        }

        private void ShowInfo(string message)
        {
            if (noGUIMode)
                return;

            MessageBox.Show(message, "EasyEDA Loader", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool ImportSelectedComponents(
            IReadOnlyList<ComponentSelection> selections,
            Action<ImportProgressEvent> progress)
        {
            if (selections == null || selections.Count == 0)
                return false;

            var currentDoc = GetCurrentDocument();

            var ctx = new CancellationTokenSource();
            var api = new EasyedaApi();

            IServerDocument tempPcbDocument = null;
            IServerDocument tempSchDocument = null;
            IPCB_Library tempPcbLib = null;
            IPCB_Library activePcbLib = null;
            ISch_Lib tempSchLib = null;
            ISch_Lib activeSchLib = null;
            string tempPcbLibraryPath = "";
            bool hadErrors = false;

            ReportImportProgress(progress, "Preparing target libraries...", 25);

            if (selections.Any(selection => selection.ImportTarget == ComponentImportTarget.TemporaryLibraries))
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string libraryPath = Path.Combine(documentsPath, "AltiumEE");
                Directory.CreateDirectory(libraryPath);
                tempPcbLibraryPath = Path.Combine(libraryPath, "EasyEDA.pcblib");
                string schLibraryPath = Path.Combine(libraryPath, "EasyEDA.schlib");

                ReportImportProgress(progress, "Opening temporary PCB library...", 27);
                tempPcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", tempPcbLibraryPath);
                AltiumApi.GlobalVars.Client.ShowDocument(tempPcbDocument);
                tempPcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();

                ReportImportProgress(progress, "Opening temporary schematic library...", 29);
                tempSchDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schLibraryPath);
                AltiumApi.GlobalVars.Client.ShowDocument(tempSchDocument);
                tempSchLib = EESCH.GetCurrentSchLibrary();
            }

            if (selections.Any(selection => selection.ImportTarget == ComponentImportTarget.ActivePcbLibrary && selection.IncludeFootprint))
            {
                activePcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
                if (activePcbLib == null)
                {
                    ReportImportProgress(progress, "Open and activate a PCB library before adding a footprint.", 30, false, true);
                    return false;
                }
            }

            if (selections.Any(selection => selection.ImportTarget == ComponentImportTarget.ActiveSchLibrary && selection.IncludeSymbol))
            {
                activeSchLib = GetActiveSchLibrary();
                if (activeSchLib == null)
                {
                    ReportImportProgress(progress, "Open and activate a schematic library before adding a symbol.", 30, false, true);
                    return false;
                }
            }

            // Process each selected component
            for (int selectionIndex = 0; selectionIndex < selections.Count; selectionIndex++)
            {
                var selection = selections[selectionIndex];
                double componentStartProgress = 30.0 + (selectionIndex * 60.0 / selections.Count);
                double componentEndProgress = 30.0 + ((selectionIndex + 1) * 60.0 / selections.Count);
                try
                {
                    var root = selection.Root;
                    var ee_footprint = root.Component.PackageDetail?.Footprint;
                    var ee_symbol = root.Component.Symbol;
                    string package = FirstNonEmpty(ee_footprint?.Head?.Parameters?.Package, selection.PartInfo?.Name, selection.PartInfo?.Part);
                    EasyedaApi.ProductInfo productInfo = selection.PartInfo?.Info;
                    string partNumber = SelectPartNumber(selection, root.Component, ee_symbol, productInfo);
                    string partName = FirstNonEmpty(partNumber, ee_symbol?.Head?.Parameters?.Name, root.Component.Title, selection.PartInfo?.Name, selection.PartInfo?.Part);
                    string mounting = InferMounting(ee_footprint);
                    EeFootprint3dModel model = selection.Include3dModel ? ee_footprint?.GetModel() : null;

                    ReportImportProgress(
                        progress,
                        $"Processing {selectionIndex + 1}/{selections.Count}: {partName}",
                        componentStartProgress);

                    // Prefetch model if we can.
                    string modelTraceIdentifier = FirstNonEmpty(partNumber, partName, model?.Uuid);
                    Task<byte[]> modelTask = model != null
                        ? ModelImportTrace.MeasureAsync("model_download_cache_read", modelTraceIdentifier, () => ModelCache.GetStepModelAsync(api, model.Uuid, ctx.Token))
                        : null;
                    Task<byte[]> rawModelTask = model != null
                        ? ModelImportTrace.MeasureAsync("raw_obj_download_cache_read", modelTraceIdentifier, () => ModelCache.GetRawObjModelAsync(api, model.Uuid, ctx.Token))
                        : null;

                    // Get product info (use cached from search if available)
                    string footprintName = FootprintMetadataSelector.SelectName(package, partNumber);
                    string footprintDescription = SelectFootprintDescription(root.Component, productInfo, partNumber, package, mounting);
                    double footprintHeight = SelectFootprintHeightMm(productInfo);
                    ReportImportProgress(
                        progress,
                        $"Metadata selected: package='{package}', part='{partNumber}', footprint='{footprintName}', description='{footprintDescription}'",
                        componentStartProgress + 2.0);

                    IPCB_Library targetPcbLib = null;
                    IServerDocument targetPcbDocument = null;
                    ISch_Lib targetSchLib = null;
                    IServerDocument targetSchDocument = null;
                    string pcbLibraryPath = "";

                    if (selection.ImportTarget == ComponentImportTarget.TemporaryLibraries)
                    {
                        targetPcbLib = tempPcbLib;
                        targetPcbDocument = tempPcbDocument;
                        targetSchLib = tempSchLib;
                        targetSchDocument = tempSchDocument;
                        pcbLibraryPath = tempPcbLibraryPath;
                    }
                    else if (selection.ImportTarget == ComponentImportTarget.ActivePcbLibrary)
                    {
                        targetPcbLib = activePcbLib;
                        targetPcbDocument = currentDoc;
                    }
                    else if (selection.ImportTarget == ComponentImportTarget.ActiveSchLibrary)
                    {
                        targetSchLib = activeSchLib;
                        targetSchDocument = currentDoc;
                    }

                        // Create PCB footprint if requested
                        if (selection.IncludeFootprint)
                        {
                            if (ee_footprint == null)
                                throw new InvalidOperationException("The selected component does not include a footprint.");
                            if (targetPcbLib == null)
                                throw new InvalidOperationException("No target PCB library is active.");

                            ReportImportProgress(progress, $"Creating PCB footprint: {footprintName}", componentStartProgress + 5.0);

                            if (targetPcbDocument != null)
                                AltiumApi.GlobalVars.Client.ShowDocument(targetPcbDocument);

                            var libComp = targetPcbLib.GetComponentByName(footprintName);
                            bool createdFootprint = false;
                            if (libComp == null)
                            {
                                libComp = EEPCB.CreateFootprintInLib(footprintName, footprintDescription, footprintHeight);
                                createdFootprint = libComp != null;
                                Trace($"PCB footprint create requested='{footprintName}' actual='{EEPCB.GetComponentPattern(libComp)}' description='{footprintDescription}'");
                            }
                            else
                            {
                                EEPCB.SetFootprintMetadata(libComp, footprintDescription, footprintHeight);
                                EEPCB.SetComponentBodyIdentifiers(libComp, partNumber);
                                Trace($"PCB footprint update requested='{footprintName}' actual='{EEPCB.GetComponentPattern(libComp)}' description='{footprintDescription}'");
                            }

                            if (createdFootprint)
                            {
                                AltiumApi.GlobalVars.PCBServer.PreProcess();
                                var footprintContext = new EeFootprintContext
                                {
                                    Box = ee_footprint.BoundingBox,
                                    Layers = ee_footprint.Layers,
                                    OriginX = ee_footprint.Head?.X,
                                    OriginY = ee_footprint.Head?.Y,
                                    CancelToken = ctx.Token,
                                    Exception = (Exception ex) =>
                                    {
                                        ReportImportProgress(progress, "Footprint warning: " + ex.Message, null, true, true);
                                        return true;
                                    },
                                    ModelTask = modelTask,
                                    RawModelTask = rawModelTask,
                                    RemoveWatermark = selection.RemoveWatermark,
                                    CleanText = selection.CleanText,
                                    ImportLcscMechanicalLayers = selection.ImportLcscMechanicalLayers,
                                    PartNumber = partNumber,
                                    CachePartNumber = selection.PartInfo?.Part,
                                    Description = footprintDescription,
                                    HeightMm = footprintHeight,
                                    ModelOffset = productInfo?.Offset,
                                };
                                ReportImportProgress(progress, $"Adding footprint primitives: {footprintName}", componentStartProgress + 12.0);
                                ee_footprint.AddToComponent(libComp, footprintContext);
                                AltiumApi.GlobalVars.PCBServer.PostProcess();
                                ReportImportProgress(progress, $"PCB footprint complete: {footprintName}", componentStartProgress + 25.0);
                            }
                        }

                        // Create schematic symbol
                        if (selection.IncludeSymbol)
                        {
                            if (ee_symbol == null)
                                throw new InvalidOperationException("The selected component does not include a schematic symbol.");
                            if (targetSchLib == null)
                                throw new InvalidOperationException("No target schematic library is active.");

                            ReportImportProgress(progress, $"Creating schematic symbol: {partName}", componentStartProgress + 32.0);

                            if (targetSchDocument != null)
                                AltiumApi.GlobalVars.Client.ShowDocument(targetSchDocument);

                            string description = SymbolImportRules.SelectSymbolDescription(
                                productInfo?.Description,
                                root.Component?.Description,
                                root.Component?.PackageDetail?.Title,
                                package,
                                partNumber,
                                mounting,
                                productInfo?.Parameters,
                                BuildFootprintDescriptionGeometry(ee_footprint));
                            string designator = EESCH.SelectRuleDesignator(ee_symbol.Head.Parameters.Pre, partName, description, package);

                            var existingComponent = targetSchLib.GetState_SchComponentByLibRef(partName);
                            if (existingComponent == null)
                            {
                                var component = EESCH.CreateComponent(partName, description, designator);
                                if (component != null)
                                {
                                    BeginSchematicLibraryEdit(targetSchDocument);
                                    try
                                    {
                                        bool isConnector = SymbolImportRules.IsConnectorDesignator(designator);
                                        SymbolDrawing.CreateComponent(targetSchLib, component, pcbLibraryPath, footprintName, ee_symbol, isConnector);

                                        EESCH.ApplyGostPropertySet(component, BuildSchematicPropertySet(ee_symbol, ee_footprint, productInfo, designator, partName, description, footprintName, package, pcbLibraryPath, mounting));
                                    }
                                    finally
                                    {
                                        EndSchematicLibraryEdit(targetSchDocument);
                                    }
                                    targetSchLib.SetState_Current_SchComponent(component);
                                    targetSchLib.GraphicallyInvalidate();
                                    targetSchDocument?.SetModified(true);
                                    ReportImportProgress(progress, $"Schematic symbol complete: {partName}", componentStartProgress + 50.0);
                                }
                            }
                        }

                        ReportImportProgress(progress, $"Finished {partName}", componentEndProgress);
                    }
                    catch (Exception ex)
                    {
                        hadErrors = true;
                        ReportImportProgress(progress, $"Failed to process component {selection.PartInfo.Name}: {ex.Message}", componentEndProgress, false, true);
                    }
                }

            ReportImportProgress(progress, "Applying import save policy...", 92);
            ImportLibrarySavePolicy.EnsureAutomaticLibrarySaveIsDisabled();
            Trace("Imported libraries left unsaved by import policy.");

            // Return to the original document we started in
            if (currentDoc != null)
            {
                ReportImportProgress(progress, "Returning to original document...", 98);
                AltiumApi.GlobalVars.Client.ShowDocument(currentDoc);
            }

            ctx.Dispose();

            if (hadErrors)
            {
                ReportImportProgress(progress, "Import finished with errors. Review the log above.", 100, false, true);
                return false;
            }

            ReportImportProgress(progress, "Import finished.", 100);
            return true;
        }

        private static void ReportImportProgress(
            Action<ImportProgressEvent> progress,
            string message,
            double? percent = null,
            bool isIndeterminate = false,
            bool isError = false)
        {
            Trace(message);
            progress?.Invoke(new ImportProgressEvent
            {
                Message = message,
                Percent = percent,
                IsIndeterminate = isIndeterminate || !percent.HasValue,
                IsError = isError
            });
        }

        private static void BeginSchematicLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PreProcess(document, "");
        }

        private static void BeginPcbLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PreProcess(document, "");
        }

        private static void EndSchematicLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PostProcess(document, "");
        }

        private static void EndPcbLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PostProcess(document, "");
        }

        private static ISch_Lib GetActiveSchLibrary()
        {
            try
            {
                var schDoc = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument();
                if (schDoc == null || schDoc.GetState_ObjectId() != SCH.TObjectId.eSchLib)
                    return null;

                return schDoc as ISch_Lib;
            }
            catch
            {
                return null;
            }
        }

        private static IServerDocument GetCurrentDocument()
        {
            try
            {
                return AltiumApi.GlobalVars.Client.GetCurrentView()?.GetOwnerDocument();
            }
            catch
            {
                return null;
            }
        }
    }
}
