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
            string pcbLibraryPath)
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
                Mounting = InferMounting(footprintData)
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

        private static string SelectFootprintDescription(ComponentInfo component, EasyedaApi.ProductInfo productInfo, string partNumber, string package)
        {
            return FirstNonEmpty(
                productInfo?.Description,
                component?.Description,
                component?.PackageDetail?.Title,
                package,
                partNumber);
        }

        private static double SelectFootprintHeightMm(EasyedaApi.ProductInfo productInfo)
        {
            double height = productInfo?.Size?.Z ?? 0;
            return height > 0 ? height : 0;
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
            if (result != DialogResult.OK || dialog.SelectedComponents.Count == 0)
                return;

            var currentDoc = GetCurrentDocument();

            var ctx = new CancellationTokenSource();
            var api = new EasyedaApi();

            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string libraryPath = Path.Combine(documentsPath, "AltiumEE");
            Directory.CreateDirectory(libraryPath);
            string pcbLibraryPath = Path.Combine(libraryPath, "EasyEDA.pcblib");
            string schLibraryPath = Path.Combine(libraryPath, "EasyEDA.schlib");

            var pcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", pcbLibraryPath);
            AltiumApi.GlobalVars.Client.ShowDocument(pcbDocument);
            var pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();

            var schDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schLibraryPath);
            AltiumApi.GlobalVars.Client.ShowDocument(schDocument);
            var schLib = EESCH.GetCurrentSchLibrary();

            // Process each selected component
            foreach (var selection in dialog.SelectedComponents)
            {
                try
                {
                    var root = selection.Root;
                    var owner_id = root.Component.Owner.Uuid;
                    var ee_footprint = root.Component.PackageDetail.Footprint;
                    var ee_symbol = root.Component.Symbol;
                    string package = ee_footprint.Head.Parameters.Package;
                    string partName = ee_symbol.Head.Parameters.Name;
                    string partNumber = SelectPartNumber(selection, root.Component, ee_symbol);
                    EeFootprint3dModel model = selection.Include3dModel ? ee_footprint.GetModel() : null;

                    // Prefetch model if we can
                    Task<byte[]> modelTask = model != null ? Task.Run(() => api.LoadModelAsync(model.Uuid, ctx.Token)) : null;
                    Task<byte[]> rawModelTask = model != null ? Task.Run(() => api.LoadRawModelAsync(model.Uuid, ctx.Token)) : null;

                    // Get product info (use cached from search if available)
                    EasyedaApi.ProductInfo productInfo = selection.PartInfo?.Info;
                    string footprintName = FirstNonEmpty(partNumber, package);
                    string footprintDescription = SelectFootprintDescription(root.Component, productInfo, partNumber, package);
                    double footprintHeight = SelectFootprintHeightMm(productInfo);

                    // Create PCB footprint if requested
                    if (selection.IncludeFootprint)
                    {
                        AltiumApi.GlobalVars.Client.ShowDocument(pcbDocument);
                        var libComp = pcbLib.GetComponentByName(footprintName);
                        bool createdFootprint = false;
                        if (libComp == null)
                        {
                            libComp = EEPCB.CreateFootprintInLib(footprintName, footprintDescription, footprintHeight);
                            createdFootprint = libComp != null;
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
                            if (dialog.SaveLibraryDocuments)
                                pcbDocument.DoFileSave("PcbLib");
                        }
                    }

                    // Create schematic symbol
                    string description = productInfo?.Description ?? partName;
                    string designator = EESCH.SelectRuleDesignator(ee_symbol.Head.Parameters.Pre, partName, description, package);

                    var existingComponent = schLib.GetState_SchComponentByLibRef(partName);
                    if (existingComponent == null)
                    {
                        var component = EESCH.CreateComponent(partName, description, designator);
                        if (schLib != null && component != null)
                        {
                            AltiumApi.GlobalVars.PCBServer.PreProcess();
                            SymbolDrawing.CreateComponent(schLib, component, pcbLibraryPath, footprintName, ee_symbol);

                            EESCH.ApplyGostPropertySet(component, BuildSchematicPropertySet(ee_symbol, ee_footprint, productInfo, designator, partName, description, footprintName, package, pcbLibraryPath));
                            AltiumApi.GlobalVars.PCBServer.PostProcess();
                            schLib.SetState_Current_SchComponent(component);
                            schLib.GraphicallyInvalidate();
                            if (dialog.SaveLibraryDocuments)
                                schDocument.DoFileSave("SchLib");
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

            // Close the library documents if requested
            if (dialog.CloseDocuments)
            {
                AltiumApi.GlobalVars.Client.CloseDocument(pcbDocument);
                AltiumApi.GlobalVars.Client.CloseDocument(schDocument);
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
