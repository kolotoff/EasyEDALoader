using Altium.Edp.Classes;
using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class TronstolE1OutputGenerator : OutputGenerator
    {
        private const string GeneratorName = "Tronstol E1 PNP";
        private readonly TronstolE1Settings settings;
        private string outputDirectory;

        public TronstolE1OutputGenerator()
            : base(GeneratorName)
        {
            settings = new TronstolE1Settings();
            OutputSettings = settings;
        }

        protected override void InternalSetOutputPath(string targetFolder)
        {
            outputDirectory = targetFolder;
        }

        protected override void InternalPredictOutputFilenames(IStrings filenames)
        {
            if (filenames == null)
                return;

            filenames.Add(ResolveOutputFilePath());
        }

        protected override bool InternalRunPropertiesForm()
        {
            using (var form = new TronstolE1PropertiesForm(
                settings.RemoveBgaSuffix,
                settings.RemoveSpaceBgaSuffix,
                settings.SkipNfComponents,
                settings.SkipDnpComponents,
                settings.SkipManualSolderingComponents,
                settings.SkipWaveSolderingComponents,
                settings.ExportPanelFiducials,
                settings.RemoveFootprintFromPartNumber,
                settings.CollapsePartNumberSpaces))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return false;

                settings.RemoveBgaSuffix = form.RemoveBgaSuffix;
                settings.RemoveSpaceBgaSuffix = form.RemoveSpaceBgaSuffix;
                settings.SkipNfComponents = form.SkipNfComponents;
                settings.SkipDnpComponents = form.SkipDnpComponents;
                settings.SkipManualSolderingComponents = form.SkipManualSolderingComponents;
                settings.SkipWaveSolderingComponents = form.SkipWaveSolderingComponents;
                settings.ExportPanelFiducials = form.ExportPanelFiducials;
                settings.RemoveFootprintFromPartNumber = form.RemoveFootprintFromPartNumber;
                settings.CollapsePartNumberSpaces = form.CollapsePartNumberSpaces;
                return true;
            }
        }

        protected override bool InternalRunGenerator()
        {
            string outputFile = ResolveOutputFilePath();
            try
            {
                IPCB_Board board = LoadBoard(DocumentPath);
                if (board == null)
                    throw new InvalidOperationException("Unable to load PCB document: " + DocumentPath);

                string directory = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                Notify_BeginGeneratingOutputFile(outputFile);
                try
                {
                    using (var writer = new StreamWriter(outputFile, false, new UTF8Encoding(false)))
                        TronstolE1Csv.Write(writer, ReadOutputPlacements(board));
                }
                finally
                {
                    Notify_FinishGeneratingOutputFile(outputFile);
                }

                return true;
            }
            catch (Exception ex)
            {
                HandleOutputerError("Tronstol E1 PNP generation failed: " + ex.Message);
                return false;
            }
        }

        private string ResolveOutputFilePath()
        {
            string explicitName = ExplicitTargetFilename;
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                if (Path.IsPathRooted(explicitName))
                    return EnsureCsvExtension(explicitName);
                return Path.Combine(ResolveOutputDirectory(), EnsureCsvExtension(explicitName));
            }

            string boardName = Path.GetFileNameWithoutExtension(DocumentPath);
            if (string.IsNullOrWhiteSpace(boardName))
                boardName = "PCB";

            string prefix = string.IsNullOrWhiteSpace(TargetPrefix) ? string.Empty : TargetPrefix;
            return Path.Combine(
                ResolveOutputDirectory(),
                EnsureCsvExtension(prefix + boardName + " Tronstol E1 PNP"));
        }

        private string ResolveOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                return outputDirectory;

            string documentDirectory = Path.GetDirectoryName(DocumentPath);
            return string.IsNullOrWhiteSpace(documentDirectory)
                ? Environment.CurrentDirectory
                : documentDirectory;
        }

        private IEnumerable<TronstolE1Placement> ReadOutputPlacements(IPCB_Board board)
        {
            if (settings.ExportPanelFiducials)
            {
                foreach (TronstolE1Placement fiducial in ReadPanelFiducials(board))
                    yield return fiducial;
            }

            foreach (TronstolE1Placement placement in ReadPlacements(board))
                yield return placement;
        }

        private static string EnsureCsvExtension(string path)
        {
            return string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + ".csv";
        }

        private static IPCB_Board LoadBoard(string documentPath)
        {
            if (string.IsNullOrWhiteSpace(documentPath))
                return null;

            IClient client = DXP.GlobalVars.Client;
            if (client == null)
                return null;

            client.StartServer("PCB");
            var pcbServer = client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
            if (pcbServer == null)
                return null;

            return pcbServer.Internal_GetPCBBoardByPath(documentPath) as IPCB_Board
                ?? pcbServer.Internal_LoadPCBBoardByPath(documentPath) as IPCB_Board;
        }

        private IEnumerable<TronstolE1Placement> ReadPanelFiducials(IPCB_Board board)
        {
            IPCB_BoardIterator iterator = null;
            try
            {
                iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
                if (iterator == null)
                    yield break;

                iterator.AddFilter_ObjectSet(CreateObjectSet((int)TObjectId.ePadObject));
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Pad pad
                        && TryParsePanelFiducialNumber(pad.GetState_Name(), out int number))
                    {
                        yield return ReadPanelFiducial(pad, number);
                    }

                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                if (iterator != null)
                    board.BoardIterator_Destroy(ref iterator);
            }
        }

        private static TronstolE1Placement ReadPanelFiducial(IPCB_Pad pad, int number)
        {
            bool isBottom = IsBottomPad(pad);
            return new TronstolE1Placement
            {
                Designator = "Fiducial" + number.ToString(CultureInfo.InvariantCulture),
                OriginalPartNumber = "PanelFiducial",
                PartNumber = "PanelFiducial",
                Footprint = FormatPadFootprint(pad, isBottom),
                CenterXMillimeters = EDP.Utils.CoordToMMs(pad.GetState_XLocation()),
                CenterYMillimeters = EDP.Utils.CoordToMMs(pad.GetState_YLocation()),
                IsBottom = isBottom,
                RotationDegrees = 0.0,
                RotationText = "0.0",
                IsPanelFiducial = true,
                PanelFiducialNumber = number
            };
        }

        private static bool TryParsePanelFiducialNumber(string name, out int number)
        {
            number = 0;
            const string prefix = "PanelFiducial";
            if (name == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = name.Substring(prefix.Length);
            if (suffix.Length == 0)
                return false;

            for (int index = 0; index < suffix.Length; index++)
            {
                if (!char.IsDigit(suffix[index]))
                    return false;
            }

            return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        private static bool IsBottomPad(IPCB_Pad pad)
        {
            return TryGetPrimitiveLayerNumber(pad, out int layerNumber)
                && IsLayer(layerNumber, TLayerConstant.eBottomLayer);
        }

        private static string FormatPadFootprint(IPCB_Pad pad, bool isBottom)
        {
            ReadPadShapeAndXSize(pad, isBottom, out int shape, out int xSize);

            return FormatPadShape(shape)
                + " "
                + EDP.Utils.CoordToMMs(xSize).ToString("0.00", CultureInfo.InvariantCulture)
                + "mm";
        }

        private static string FormatPadShape(int shape)
        {
            if (shape == (int)TShape.eRounded)
                return "Round";
            if (shape == (int)TShape.eRectangular)
                return "Rectangular";
            if (shape == (int)TShape.eRoundedRectangular
                || shape == (int)TShape.eRoundRectShape)
            {
                return "RoundedRectangular";
            }
            if (shape == (int)TShape.eCustomShape)
                return "Custom";

            return "Shape" + shape.ToString(CultureInfo.InvariantCulture);
        }

        private static void ReadPadShapeAndXSize(
            IPCB_Pad pad,
            bool preferBottom,
            out int shape,
            out int xSize)
        {
            shape = preferBottom
                ? pad.Internal_GetState_BotShape()
                : pad.Internal_GetState_TopShape();
            xSize = preferBottom
                ? pad.GetState_BotXSize()
                : pad.GetState_TopXSize();
            if (xSize > 0)
                return;

            shape = preferBottom
                ? pad.Internal_GetState_TopShape()
                : pad.Internal_GetState_BotShape();
            xSize = preferBottom
                ? pad.GetState_TopXSize()
                : pad.GetState_BotXSize();
            if (xSize > 0)
                return;

            shape = pad.Internal_GetState_MidShape();
            xSize = pad.GetState_MidXSize();
        }

        private IEnumerable<TronstolE1Placement> ReadPlacements(IPCB_Board board)
        {
            Dictionary<string, CompiledComponentData> compiledComponents = ReadCompiledComponents(board);
            IPCB_BoardIterator iterator = null;
            try
            {
                iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
                if (iterator == null)
                    yield break;

                iterator.AddFilter_ObjectSet(CreateObjectSet((int)TObjectId.eComponentObject));
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Component component)
                    {
                        string designator = ReadText(
                            component.Internal_GetState_Name(),
                            component.GetState_SourceDesignator());
                        compiledComponents.TryGetValue(designator, out CompiledComponentData compiledComponent);
                        if (compiledComponent == null
                            || (!compiledComponent.IsNoBom
                                && !settings.ShouldSkipComment(compiledComponent.Comment)
                                && !settings.ShouldSkipSolderingType(compiledComponent.SolderingType)))
                        {
                            yield return ReadPlacement(
                                component,
                                designator,
                                compiledComponent?.PartNumber,
                                settings);
                        }
                    }
                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                if (iterator != null)
                    board.BoardIterator_Destroy(ref iterator);
            }
        }

        private static TronstolE1Placement ReadPlacement(
            IPCB_Component component,
            string designator,
            string partNumber,
            TronstolE1Settings settings)
        {
            string footprint = component.GetState_Pattern() ?? string.Empty;

            return new TronstolE1Placement
            {
                Designator = designator,
                OriginalPartNumber = partNumber,
                PartNumber = settings.FormatPartNumber(partNumber, footprint),
                Footprint = settings.FormatFootprintName(footprint),
                CenterXMillimeters = EDP.Utils.CoordToMMs(component.GetState_XLocation()),
                CenterYMillimeters = EDP.Utils.CoordToMMs(component.GetState_YLocation()),
                IsBottom = IsBottomComponent(component),
                RotationDegrees = component.GetState_Rotation()
            };
        }

        private static bool IsBottomComponent(IPCB_Component component)
        {
            if (component == null)
                return false;

            try
            {
                if (component.GetState_FlippedOnLayer())
                    return true;
            }
            catch
            {
            }

            int topCount = 0;
            int bottomCount = 0;
            foreach (IPCB_Primitive primitive in EnumerateComponentPrimitives(component))
            {
                if (!TryGetPrimitiveLayerNumber(primitive, out int layerNumber))
                    continue;

                if (IsLayer(layerNumber, TLayerConstant.eBottomLayer))
                    bottomCount++;
                else if (IsLayer(layerNumber, TLayerConstant.eTopLayer))
                    topCount++;
            }

            return bottomCount > topCount;
        }

        private static IEnumerable<IPCB_Primitive> EnumerateComponentPrimitives(IPCB_Component component)
        {
            if (!(component is IPCB_Group group))
                yield break;

            IPCB_GroupIterator iterator = null;
            try
            {
                iterator = group.Internal_GroupIterator_Create() as IPCB_GroupIterator;
                if (iterator == null)
                    yield break;

                iterator.SetState_FilterAll();
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Primitive primitive)
                        yield return primitive;

                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                if (iterator != null)
                    group.GroupIterator_Destroy(ref iterator);
            }
        }

        private static bool TryGetPrimitiveLayerNumber(IPCB_Primitive primitive, out int layerNumber)
        {
            layerNumber = 0;
            if (primitive == null)
                return false;

            try
            {
                IV7_Layer v7Layer = primitive.Internal_GetState_V7Layer();
                if (v7Layer != null)
                {
                    layerNumber = new V7_Layer(v7Layer).Number();
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                layerNumber = primitive.Internal_GetState_Layer();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLayer(int layerNumber, TLayerConstant layer)
        {
            if (layerNumber == (int)layer)
                return true;

            try
            {
                return layerNumber == new V7_Layer(layer).Number();
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, CompiledComponentData> ReadCompiledComponents(IPCB_Board board)
        {
            var result = new Dictionary<string, CompiledComponentData>(StringComparer.OrdinalIgnoreCase);
            if (!(board is IPCB_BoardEx boardEx))
                return result;

            var fullComponents = boardEx.Internal_GetState_FullComponents() as IPCB_FullComponents;
            var components = fullComponents?.Internal_GetComponentsForCurrentVariant() as IPCB_FullComponentList
                ?? fullComponents?.Internal_GetComponentsForAllVariants() as IPCB_FullComponentList;
            if (components == null)
                return result;

            for (int index = 0; index < components.GetCount(); index++)
            {
                var component = components.Internal_GetItem(index) as IPCB_FullComponent;
                if (component == null || string.IsNullOrWhiteSpace(component.GetDesignator()))
                    continue;

                string partNumber = ReadParameterValue(
                    component.Internal_GetParameters() as IPCB_ParameterList,
                    "PartNumber");
                string solderingType = ReadParameterValue(
                    component.Internal_GetParameters() as IPCB_ParameterList,
                    "SolderingType");
                int componentKind = component.Internal_GetKind();
                result[component.GetDesignator()] = new CompiledComponentData
                {
                    PartNumber = partNumber,
                    Comment = component.GetComment() ?? string.Empty,
                    SolderingType = solderingType,
                    IsNoBom = componentKind == (int)EDP.TComponentKind.eComponentKind_NetTie_NoBOM
                        || componentKind == (int)EDP.TComponentKind.eComponentKind_Standard_NoBOM
                };
            }

            return result;
        }

        private static string ReadParameterValue(IPCB_ParameterList parameters, string name)
        {
            if (parameters == null || string.IsNullOrWhiteSpace(name))
                return string.Empty;

            for (int index = 0; index < parameters.GetCount(); index++)
            {
                var parameter = parameters.Internal_GetByIndex(index) as IPCB_Parameter;
                if (parameter != null
                    && string.Equals(parameter.GetName(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return parameter.GetValue() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private sealed class CompiledComponentData
        {
            public string PartNumber { get; set; }
            public string Comment { get; set; }
            public string SolderingType { get; set; }
            public bool IsNoBom { get; set; }
        }

        private static string ReadText(object textObject, string fallback)
        {
            if (textObject is IPCB_Text text)
            {
                string value = FirstNonEmpty(
                    text.GetState_ConvertedString(),
                    text.GetState_Text(),
                    text.GetState_UnderlyingString());
                if (!string.IsNullOrWhiteSpace(value)
                    && !string.Equals(value, ".Designator", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, ".Comment", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return fallback ?? string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return string.Empty;
        }

        private static ITransportSet CreateObjectSet(int objectId)
        {
            var set = new GenericSet();
            int[] mask = set.Mask;
            int index = objectId / 32;
            if (index >= 0 && index < mask.Length)
                mask[index] |= unchecked((int)(1u << (objectId % 32)));
            return new TransportSet(set);
        }
    }
}
