using DXP;
using PCB;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly List<CommandProc> registeredCommandProcs = new List<CommandProc>();

        public EasyEDALoaderModule(IClient argClient)
          : base(argClient, "EasyEDA-Loader")
        {
            noGUIMode = argClient.ProductInfo().SupportsUIFeature("NoGUI", false);
            Trace("Module constructed.");
        }

        protected override IServerDocument NewDocumentInstance(string argKind, string argFileName) => (IServerDocument)null;

        protected override void InitializeCommands()
        {
            Trace("InitializeCommands.");
            RegisterCommand("EasyEDARun", new CommandProc(Run));
            RegisterCommand("EasyEDA-Loader:EasyEDARun", new CommandProc(Run));
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
                Package = CleanPropertyValue(package),
                Mounting = CleanPropertyValue(mounting)
            };
        }

        private static string SelectPartNumber(ComponentSelection selection, ComponentInfo component, SymbolData symbolData)
        {
            return FirstNonEmpty(
                selection?.PartInfo?.Name,
                symbolData?.Head?.Parameters?.ManufacturerPart,
                symbolData?.Head?.Parameters?.Name,
                component?.Title,
                selection?.PartInfo?.Part,
                component?.Lcsc?.Number,
                component?.Szlcsc?.Number);
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
            if (string.IsNullOrWhiteSpace(value) || value == "-")
                return "";

            return value.Trim();
        }

        private void Run(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("Run entered.");
            Dialog dialog = new Dialog();
            DialogResult result = dialog.ShowDialog();
            var selections = dialog.SelectedComponents;
            if (result != DialogResult.OK || selections == null || selections.Count == 0)
                return;

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

            if (selections.Exists(selection => selection.ImportTarget == ComponentImportTarget.TemporaryLibraries))
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string libraryPath = Path.Combine(documentsPath, "AltiumEE");
                Directory.CreateDirectory(libraryPath);
                tempPcbLibraryPath = Path.Combine(libraryPath, "EasyEDA.pcblib");
                string schLibraryPath = Path.Combine(libraryPath, "EasyEDA.schlib");

                tempPcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", tempPcbLibraryPath);
                AltiumApi.GlobalVars.Client.ShowDocument(tempPcbDocument);
                tempPcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();

                tempSchDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schLibraryPath);
                AltiumApi.GlobalVars.Client.ShowDocument(tempSchDocument);
                tempSchLib = EESCH.GetCurrentSchLibrary();
            }

            if (selections.Exists(selection => selection.ImportTarget == ComponentImportTarget.ActivePcbLibrary && selection.IncludeFootprint))
            {
                activePcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
                if (activePcbLib == null)
                {
                    MessageBox.Show("Open and activate a PCB library before adding a footprint.", "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    return;
                }
            }

            if (selections.Exists(selection => selection.ImportTarget == ComponentImportTarget.ActiveSchLibrary && selection.IncludeSymbol))
            {
                activeSchLib = GetActiveSchLibrary();
                if (activeSchLib == null)
                {
                    MessageBox.Show("Open and activate a schematic library before adding a symbol.", "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    return;
                }
            }

            // Process each selected component
            foreach (var selection in selections)
            {
                try
                {
                    var root = selection.Root;
                    var ee_footprint = root.Component.PackageDetail?.Footprint;
                    var ee_symbol = root.Component.Symbol;
                    string package = FirstNonEmpty(ee_footprint?.Head?.Parameters?.Package, selection.PartInfo?.Name, selection.PartInfo?.Part);
                    string partName = FirstNonEmpty(ee_symbol?.Head?.Parameters?.Name, root.Component.Title, selection.PartInfo?.Name, selection.PartInfo?.Part);
                    string partNumber = SelectPartNumber(selection, root.Component, ee_symbol);
                    string mounting = InferMounting(ee_footprint);
                    EeFootprint3dModel model = selection.Include3dModel ? ee_footprint?.GetModel() : null;

                    // Prefetch model if we can
                    Task<byte[]> modelTask = model != null ? ModelCache.GetStepModelAsync(api, model.Uuid, ctx.Token) : null;
                    Task<byte[]> rawModelTask = model != null ? ModelCache.GetRawObjModelAsync(api, model.Uuid, ctx.Token) : null;

                    // Get product info (use cached from search if available)
                    EasyedaApi.ProductInfo productInfo = selection.PartInfo?.Info;
                    string footprintName = FirstNonEmpty(partNumber, package);
                    string footprintDescription = SelectFootprintDescription(root.Component, productInfo, partNumber, package, mounting);
                    double footprintHeight = SelectFootprintHeightMm(productInfo);

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

                        if (targetPcbDocument != null)
                            AltiumApi.GlobalVars.Client.ShowDocument(targetPcbDocument);

                        var libComp = targetPcbLib.GetComponentByName(footprintName);
                        bool createdFootprint = false;
                        if (libComp == null)
                        {
                            libComp = EEPCB.CreateFootprintInLib(footprintName, footprintDescription, footprintHeight);
                            createdFootprint = libComp != null;
                        }
                        else
                        {
                            EEPCB.SetFootprintMetadata(libComp, footprintDescription, footprintHeight);
                            EEPCB.SetComponentBodyIdentifiers(libComp, partNumber);
                        }

                        if (createdFootprint)
                        {
                            AltiumApi.GlobalVars.PCBServer.PreProcess();
                            var footprintContext = new EeFootprintContext
                            {
                                Box = ee_footprint.BoundingBox,
                                Layers = ee_footprint.Layers,
                                CancelToken = ctx.Token,
                                Exception = (Exception ex) =>
                                {
                                    // Log problems here?
                                    return true;
                                },
                                ModelTask = modelTask,
                                RawModelTask = rawModelTask,
                                RemoveWatermark = selection.RemoveWatermark,
                                PartNumber = partNumber,
                                Description = footprintDescription,
                                HeightMm = footprintHeight,
                            };
                            ee_footprint.AddToComponent(libComp, footprintContext);
                            AltiumApi.GlobalVars.PCBServer.PostProcess();
                        }
                    }

                    // Create schematic symbol
                    if (selection.IncludeSymbol)
                    {
                        if (ee_symbol == null)
                            throw new InvalidOperationException("The selected component does not include a schematic symbol.");
                        if (targetSchLib == null)
                            throw new InvalidOperationException("No target schematic library is active.");

                        if (targetSchDocument != null)
                            AltiumApi.GlobalVars.Client.ShowDocument(targetSchDocument);

                        string description = productInfo?.Description ?? partName;
                        string designator = EESCH.SelectRuleDesignator(ee_symbol.Head.Parameters.Pre, partName, description, package);

                        var existingComponent = targetSchLib.GetState_SchComponentByLibRef(partName);
                        if (existingComponent == null)
                        {
                            var component = EESCH.CreateComponent(partName, description, designator);
                            if (component != null)
                            {
                                AltiumApi.GlobalVars.PCBServer.PreProcess();
                                SymbolDrawing.CreateComponent(targetSchLib, component, pcbLibraryPath, footprintName, ee_symbol);

                                EESCH.ApplyGostPropertySet(component, BuildSchematicPropertySet(ee_symbol, ee_footprint, productInfo, designator, partName, description, footprintName, package, pcbLibraryPath, mounting));
                                AltiumApi.GlobalVars.PCBServer.PostProcess();
                                targetSchLib.SetState_Current_SchComponent(component);
                                targetSchLib.GraphicallyInvalidate();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to process component {selection.PartInfo.Name}: {ex.Message}", "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
            }

            // Return to the original document we started in
            if (currentDoc != null)
                AltiumApi.GlobalVars.Client.ShowDocument(currentDoc);

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
