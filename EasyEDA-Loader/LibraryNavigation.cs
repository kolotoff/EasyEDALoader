using DXP;
using EDP;
using PCB;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;

namespace EasyEDA_Loader
{
    internal static class LibraryNavigation
    {
        public static void OpenSelectedSchematicComponentLibrary(IServerDocumentView commandView)
        {
            ISch_Document schematic = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument() as ISch_Document;
            if (schematic == null || schematic.GetState_ObjectId() != SCH.TObjectId.eSheet)
                throw new InvalidOperationException("Open a schematic document before running Open symbol library.");

            ISch_Component component = FindSelectedSchematicComponent(schematic);
            if (component == null)
                throw new InvalidOperationException("Select a schematic component before running Open symbol library.");

            string symbolName = FirstNonEmpty(
                component.GetState_LibReference(),
                component.GetState_DesignItemId());
            string currentDocumentPath = schematic.GetState_DocumentName();
            string managedLibraryPath = "";
            TryResolveSchematicSymbolWithIntegratedLibraryManager(
                component,
                ref symbolName,
                out managedLibraryPath);
            string libraryPath = ResolveLibraryPath(
                ".SchLib",
                currentDocumentPath,
                managedLibraryPath,
                component.GetState_LibraryPath(),
                component.GetState_SourceLibraryName(),
                component.GetState_LibraryIdentifier());

            ISch_Lib library = OpenSchematicLibrary(libraryPath);
            ISch_Component libraryComponent = library.GetState_SchComponentByLibRef(symbolName);
            if (libraryComponent == null)
                throw new InvalidOperationException(
                    $"Opened schematic library '{libraryPath}', but symbol '{symbolName}' was not found.");

            ActivateSchematicLibraryComponent(library, libraryComponent, symbolName);
            EasyEDALoaderModule.Trace(
                $"Open symbol library completed. Library='{libraryPath}' Symbol='{symbolName}'.");
        }

        private static void ActivateSchematicLibraryComponent(
            ISch_Lib library,
            ISch_Component libraryComponent,
            string symbolName)
        {
            if (library == null || libraryComponent == null)
                return;

            try
            {
                string parameters = "";
                DXP.Utils.RunCommand("Sch:FirstComponentLibraryEditor", ref parameters);

                var visitedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < 100000; index++)
                {
                    ISch_Component currentComponent = library.GetState_Current_SchComponent();
                    string currentName = currentComponent?.GetState_LibReference() ?? "";
                    if (ReferenceEquals(currentComponent, libraryComponent) ||
                        string.Equals(currentName, symbolName, StringComparison.OrdinalIgnoreCase))
                    {
                        EasyEDALoaderModule.Trace(
                            $"Selected schematic library component through native editor commands. " +
                            $"Symbol='{symbolName}' Index={index}.");
                        return;
                    }

                    if (currentComponent == null ||
                        string.IsNullOrWhiteSpace(currentName) ||
                        !visitedComponents.Add(currentName))
                        break;

                    parameters = "";
                    DXP.Utils.RunCommand("Sch:NextComponentLibraryEditor", ref parameters);
                }
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    "Native schematic library component selection failed: " + ex.Message);
            }

            library.SetState_Current_SchComponent(libraryComponent);
            library.TransferComponentsPrimitivesToEditor();
            library.GraphicallyInvalidate();
            EasyEDALoaderModule.Trace(
                $"Selected schematic library component through API fallback. Symbol='{symbolName}'.");
        }

        private static bool TryResolveSchematicSymbolWithIntegratedLibraryManager(
            ISch_Component component,
            ref string symbolName,
            out string libraryPath)
        {
            libraryPath = "";
            if (component == null)
                return false;

            try
            {
                IIntegratedLibraryManager manager = TryLoadIntegratedLibraryManager();
                if (manager == null)
                {
                    EasyEDALoaderModule.Trace(
                        "EDP.Utils.LoadIntegratedLibraryManager returned no manager.");
                    return false;
                }

                string resolvedSymbolName = "";
                bool found = manager.FindComponentSymbol(
                    (int)component.GetState_LibIdentifierKind(),
                    component.GetState_LibraryIdentifier(),
                    component.GetState_DesignItemId(),
                    out libraryPath,
                    out resolvedSymbolName);
                if (!found || string.IsNullOrWhiteSpace(libraryPath))
                {
                    libraryPath = "";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(resolvedSymbolName))
                    symbolName = resolvedSymbolName;

                EasyEDALoaderModule.Trace(
                    $"Integrated library manager resolved symbol. Library='{libraryPath}' Symbol='{symbolName}'.");
                return true;
            }
            catch (Exception ex)
            {
                libraryPath = "";
                EasyEDALoaderModule.Trace(
                    "Integrated library manager symbol resolution failed: " + ex.Message);
                return false;
            }
        }

        public static void OpenSelectedPcbComponentLibrary(IServerDocumentView commandView)
        {
            IPCB_Board board = LayoutDuplicationPcbAccess.GetCurrentBoard(commandView);
            if (board == null)
                throw new InvalidOperationException("Open a PCB document before running Open footprint library.");

            IPCB_Component component = FindSelectedPcbComponent(board);
            if (component == null)
                throw new InvalidOperationException("Select a PCB component before running Open footprint library.");

            string footprintName = component.GetState_Pattern();
            if (TryOpenPcbLibraryWithNativeCommand(footprintName))
                return;

            string libraryPath = ResolveLibraryPath(
                ".PcbLib",
                board.GetState_FileName(),
                component.GetState_SourceFootprintLibrary());

            IPCB_Library library = OpenPcbLibrary(libraryPath);
            IPCB_LibComponent footprint = library.GetComponentByName(footprintName);
            if (footprint == null)
                throw new InvalidOperationException(
                    $"Opened PCB library '{libraryPath}', but footprint '{footprintName}' was not found.");

            library.SetState_CurrentComponent(footprint);
            EEPCB.GetPcbGroupBoard(footprint)?.ViewManager_FullUpdate();
            EasyEDALoaderModule.Trace(
                $"Open footprint library completed. Library='{libraryPath}' Footprint='{footprintName}'.");
        }

        internal static string TryResolvePcbLibraryPath(IPCB_Board board, IPCB_Component component)
        {
            if (board == null || component == null)
                return "";

            try
            {
                return ResolveLibraryPath(
                    ".PcbLib",
                    board.GetState_FileName(),
                    component.GetState_SourceFootprintLibrary());
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    $"Could not resolve source PCB library for footprint '{component.GetState_Pattern()}': {ex.Message}");
                return "";
            }
        }

        private static bool TryOpenPcbLibraryWithNativeCommand(string footprintName)
        {
            try
            {
                string parameters = "";
                DXP.Utils.RunCommand("PCB:GotoLibraryComponent", ref parameters);

                IPCB_Library library = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
                IPCB_Group currentFootprint = EEPCB.GetCurrentPcbLibComponent();
                string currentFootprintName = EEPCB.GetComponentPattern(currentFootprint);
                if (library == null ||
                    !string.Equals(currentFootprintName, footprintName, StringComparison.OrdinalIgnoreCase))
                {
                    EasyEDALoaderModule.Trace(
                        $"PCB:GotoLibraryComponent did not activate footprint '{footprintName}'.");
                    return false;
                }

                currentFootprint.GetState_Board()?.ViewManager_FullUpdate();
                EasyEDALoaderModule.Trace(
                    $"Open footprint library completed through PCB:GotoLibraryComponent. Footprint='{footprintName}'.");
                return true;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("PCB:GotoLibraryComponent failed: " + ex.Message);
                return false;
            }
        }

        private static ISch_Component FindSelectedSchematicComponent(ISch_Document schematic)
        {
            if (schematic == null)
                return null;

            DXP.ITransportSet componentObjectSet = CreateSchematicObjectSet(
                (int)SCH.TObjectId.eSchComponent);

            SCH.CoordRect selectedBounds;
            try
            {
                selectedBounds = SCH.ISch_DocumentHelper.BoundingRectangle_Selected(schematic);
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    "Could not read schematic selected bounds: " + ex.Message);
                return null;
            }

            int left = Math.Min(selectedBounds.GetLeft(), selectedBounds.GetRight());
            int right = Math.Max(selectedBounds.GetLeft(), selectedBounds.GetRight());
            int bottom = Math.Min(selectedBounds.GetBottom(), selectedBounds.GetTop());
            int top = Math.Max(selectedBounds.GetBottom(), selectedBounds.GetTop());
            EasyEDALoaderModule.Trace(
                $"Schematic selected bounds: Left={left}, Bottom={bottom}, Right={right}, Top={top}.");

            ISch_Iterator iterator = null;
            try
            {
                iterator = schematic.SchIterator_Create();
                iterator.AddFilter_ObjectSet(componentObjectSet);
                iterator.AddFilter_Area(left, bottom, right, top);
                ISch_BasicContainer item = iterator.FirstSchObject();
                while (item != null)
                {
                    if (item is ISch_Component component)
                    {
                        EasyEDALoaderModule.Trace(
                            "Resolved selected schematic component through Altium selection bounds.");
                        return component;
                    }

                    item = iterator.NextSchObject();
                }
            }
            finally
            {
                if (iterator != null)
                    schematic.SchIterator_Destroy(ref iterator);
            }

            EasyEDALoaderModule.Trace(
                "No schematic component was returned by the selected-bounds iterator.");
            return null;
        }

        private static DXP.ITransportSet CreateSchematicObjectSet(params int[] objectIds)
        {
            var set = new DXP.GenericSet();
            int[] mask = set.Mask;
            foreach (int objectId in objectIds ?? Array.Empty<int>())
            {
                if (objectId < 0)
                    continue;

                int index = objectId / 32;
                if (index >= mask.Length)
                    continue;

                mask[index] |= unchecked((int)(1u << (objectId % 32)));
            }

            return new DXP.TransportSet(set);
        }

        private static IPCB_Component FindSelectedPcbComponent(IPCB_Board board)
        {
            foreach (object selectedObject in LayoutDuplicationPcbAccess.GetSelectedObjects(board))
            {
                if (selectedObject is IPCB_Component component)
                    return component;

                if (selectedObject is IPCB_Primitive primitive &&
                    primitive.Internal_GetState_Component() is IPCB_Component parentComponent)
                    return parentComponent;

                object parent = LayoutDuplicationPcbAccess.Invoke(selectedObject, "Internal_GetState_Component")
                    ?? LayoutDuplicationPcbAccess.Invoke(selectedObject, "GetState_Component");
                if (parent is IPCB_Component reflectedParent)
                    return reflectedParent;
            }

            return null;
        }

        private static ISch_Lib OpenSchematicLibrary(string libraryPath)
        {
            IServerDocument document = OpenAndShowDocument("SchLib", libraryPath);
            ISch_Lib library = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument() as ISch_Lib;
            if (library == null)
                throw new InvalidOperationException($"Could not activate schematic library '{libraryPath}'.");

            document.Focus();
            return library;
        }

        private static IPCB_Library OpenPcbLibrary(string libraryPath)
        {
            IServerDocument document = OpenAndShowDocument("PcbLib", libraryPath);
            IPCB_Library library = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (library == null)
                throw new InvalidOperationException($"Could not activate PCB library '{libraryPath}'.");

            document.Focus();
            return library;
        }

        private static IServerDocument OpenAndShowDocument(string documentKind, string libraryPath)
        {
            IClient client = AltiumApi.GlobalVars.Client;
            IServerDocument document = client.GetDocumentByPath(libraryPath)
                ?? client.OpenDocument(documentKind, libraryPath);
            if (document == null)
                throw new InvalidOperationException($"Could not open library '{libraryPath}'.");

            client.ShowDocument(document);
            document.Focus();
            return document;
        }

        private static string ResolveLibraryPath(
            string expectedExtension,
            string currentDocumentPath,
            params string[] references)
        {
            var candidates = new List<string>();
            var referenceDirectories = new List<string>();
            foreach (string reference in references)
            {
                AddCandidate(candidates, reference, expectedExtension);
                AddReferenceDirectory(referenceDirectories, reference);
            }

            string currentDirectory = GetDirectoryNameSafely(currentDocumentPath);
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);

                if (!string.IsNullOrWhiteSpace(currentDirectory))
                {
                    string localPath = Path.Combine(currentDirectory, candidate);
                    if (File.Exists(localPath))
                        return Path.GetFullPath(localPath);
                }
            }

            foreach (string referenceDirectory in referenceDirectories)
            {
                foreach (string candidate in candidates)
                {
                    string referencedPath = Path.Combine(referenceDirectory, Path.GetFileName(candidate));
                    if (File.Exists(referencedPath))
                        return Path.GetFullPath(referencedPath);
                }
            }

            string availableLibraryPath = FindAvailableLibraryPath(
                candidates,
                expectedExtension,
                currentDocumentPath);
            if (!string.IsNullOrWhiteSpace(availableLibraryPath))
                return availableLibraryPath;

            foreach (string projectDocumentPath in EnumerateProjectDocumentPaths())
            {
                if (!projectDocumentPath.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (string candidate in candidates)
                {
                    if (string.Equals(
                        Path.GetFileName(projectDocumentPath),
                        Path.GetFileName(candidate),
                        StringComparison.OrdinalIgnoreCase))
                        return Path.GetFullPath(projectDocumentPath);
                }
            }

            string referenceText = candidates.Count == 0
                ? "(the selected component has no library reference)"
                : string.Join(", ", candidates);
            throw new InvalidOperationException(
                $"Could not locate the referenced {expectedExtension} library: {referenceText}.");
        }

        private static string FindAvailableLibraryPath(
            IReadOnlyList<string> candidates,
            string expectedExtension,
            string currentDocumentPath)
        {
            IIntegratedLibraryManager manager = TryLoadIntegratedLibraryManager();
            if (manager == null || candidates == null || candidates.Count == 0)
                return "";

            var libraryPaths = new List<string>();
            try
            {
                for (int index = 0; index < manager.AvailableLibraryCount(); index++)
                    AddDistinctText(libraryPaths, manager.AvailableLibraryPath(index));

                for (int index = 0; index < manager.InstalledLibraryCount(); index++)
                    AddDistinctText(libraryPaths, manager.InstalledLibraryPath(index));
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    "Could not enumerate Altium available libraries: " + ex.Message);
            }

            foreach (string configuredPath in libraryPaths)
            {
                if (!configuredPath.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase) ||
                    !MatchesCandidateFileName(configuredPath, candidates))
                    continue;

                string resolvedPath = ResolveConfiguredLibraryPath(
                    manager,
                    configuredPath,
                    currentDocumentPath);
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    EasyEDALoaderModule.Trace(
                        $"Resolved Altium available library '{configuredPath}' to '{resolvedPath}'.");
                    return resolvedPath;
                }
            }

            foreach (string candidate in candidates)
            {
                string resolvedPath = ResolveConfiguredLibraryPath(
                    manager,
                    candidate,
                    currentDocumentPath);
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    EasyEDALoaderModule.Trace(
                        $"Resolved library reference '{candidate}' through Altium's library cache to '{resolvedPath}'.");
                    return resolvedPath;
                }
            }

            return "";
        }

        private static string ResolveConfiguredLibraryPath(
            IIntegratedLibraryManager manager,
            string configuredPath,
            string currentDocumentPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return "";

            string cleanedPath = configuredPath.Trim().Trim('"');
            if (File.Exists(cleanedPath))
                return Path.GetFullPath(cleanedPath);

            string defaultLibraryPath = GetDefaultLibraryPath();
            if (!string.IsNullOrWhiteSpace(defaultLibraryPath))
            {
                try
                {
                    string relativeToDefault = Path.Combine(defaultLibraryPath, cleanedPath);
                    if (File.Exists(relativeToDefault))
                        return Path.GetFullPath(relativeToDefault);
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace(
                        $"Could not combine default library path '{defaultLibraryPath}' with '{cleanedPath}': {ex.Message}");
                }
            }

            try
            {
                string cachedFullPath = "";
                if (manager.GetCachedFullFilePathFromExplicitFileName(
                    "",
                    cleanedPath,
                    ref cachedFullPath) &&
                    File.Exists(cachedFullPath))
                    return Path.GetFullPath(cachedFullPath);
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    $"Altium library-cache lookup failed for '{cleanedPath}': {ex.Message}");
            }

            return "";
        }

        private static string GetDefaultLibraryPath()
        {
            try
            {
                IWorkspace workspace = AltiumApi.GlobalVars.Workspace;
                IWorkspacePreferences preferences = workspace?.DM_Preferences();
                return preferences?.GetDefaultLibraryPath() ?? "";
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    "Could not read Altium's default library path: " + ex.Message);
                return "";
            }
        }

        private static bool MatchesCandidateFileName(
            string libraryPath,
            IReadOnlyList<string> candidates)
        {
            string libraryFileName = Path.GetFileName(libraryPath);
            foreach (string candidate in candidates)
            {
                if (string.Equals(
                    libraryFileName,
                    Path.GetFileName(candidate),
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static IIntegratedLibraryManager TryLoadIntegratedLibraryManager()
        {
            try
            {
                return EDP.Utils.LoadIntegratedLibraryManager();
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    "Could not load Altium integrated library manager: " + ex.Message);
                return null;
            }
        }

        private static void AddDistinctText(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            foreach (string existing in values)
            {
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            values.Add(value);
        }

        private static void AddCandidate(List<string> candidates, string value, string expectedExtension)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string candidate = value.Trim().Trim('"');
            if (candidate.EndsWith(".IntLib", StringComparison.OrdinalIgnoreCase) ||
                candidate.EndsWith(".DbLib", StringComparison.OrdinalIgnoreCase) ||
                candidate.EndsWith(".SVNDbLib", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(Path.GetExtension(candidate)))
                candidate += expectedExtension;
            if (!candidate.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (string existing in candidates)
            {
                if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            candidates.Add(candidate);
        }

        private static void AddReferenceDirectory(List<string> directories, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string reference = value.Trim().Trim('"');
            string directory = "";
            try
            {
                if (Directory.Exists(reference))
                    directory = reference;
                else if (!string.IsNullOrWhiteSpace(Path.GetExtension(reference)))
                    directory = Path.GetDirectoryName(reference) ?? "";
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(directory))
                return;

            foreach (string existing in directories)
            {
                if (string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            directories.Add(directory);
        }

        private static IEnumerable<string> EnumerateProjectDocumentPaths()
        {
            IWorkspace workspace = AltiumApi.GlobalVars.Workspace;
            if (workspace == null)
                yield break;

            for (int projectIndex = 0; projectIndex < workspace.DM_ProjectCount(); projectIndex++)
            {
                IProject project = workspace.DM_Projects(projectIndex);
                if (project == null)
                    continue;

                for (int documentIndex = 0; documentIndex < project.DM_LogicalDocumentCount(); documentIndex++)
                {
                    IDocument document = project.DM_LogicalDocuments(documentIndex);
                    string path = document?.DM_FullPath();
                    if (!string.IsNullOrWhiteSpace(path))
                        yield return path;
                }
            }
        }

        private static string GetDirectoryNameSafely(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            try
            {
                return Path.GetDirectoryName(path) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }
    }
}
