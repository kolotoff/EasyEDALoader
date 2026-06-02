using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace EasyEDA_Loader
{
    /// <summary>
    /// WinForms wrapper for the WPF DialogWindow to maintain compatibility with existing code
    /// </summary>
    public class Dialog
    {
        private DialogWindow wpfDialog;
        private readonly Func<IReadOnlyList<ComponentSelection>, Action<ImportProgressEvent>, bool> importExecutor;

        public List<ComponentSelection> SelectedComponents => wpfDialog?.SelectedComponents;
        public bool RemoveWatermark => wpfDialog?.RemoveWatermark ?? true;

        public Dialog(Func<IReadOnlyList<ComponentSelection>, Action<ImportProgressEvent>, bool> importExecutor = null)
        {
            this.importExecutor = importExecutor;
            wpfDialog = new DialogWindow();
            wpfDialog.ImportExecutor = this.importExecutor;
            wpfDialog.ShowActivated = true;
            wpfDialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            IntPtr ownerHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (ownerHandle != IntPtr.Zero)
                new WindowInteropHelper(wpfDialog).Owner = ownerHandle;

            wpfDialog.Loaded += (sender, args) =>
            {
                wpfDialog.Activate();
                wpfDialog.Topmost = true;
                wpfDialog.Topmost = false;
                wpfDialog.Focus();
            };
        }

        public DialogResult ShowDialog()
        {
            // Show the WPF dialog and convert the result to WinForms DialogResult
            bool? result = wpfDialog.ShowDialog();
            
            if (result == true)
                return DialogResult.OK;
            else if (result == false)
                return DialogResult.Cancel;
            else
                return DialogResult.None;
        }
    }

    public class ComponentSelection
    {
        public EasyedaApi.PartInfo PartInfo { get; set; }
        public Root Root { get; set; }
        public bool Include3dModel { get; set; }
        public bool IncludeFootprint { get; set; }
        public bool IncludeSymbol { get; set; }
        public bool RemoveWatermark { get; set; }
        public ComponentImportTarget ImportTarget { get; set; }
    }

    public sealed class ImportProgressEvent
    {
        public string Message { get; set; }
        public double? Percent { get; set; }
        public bool IsIndeterminate { get; set; }
        public bool IsError { get; set; }
        public bool AddToLog { get; set; } = true;
    }

    public enum ComponentImportTarget
    {
        TemporaryLibraries,
        ActivePcbLibrary,
        ActiveSchLibrary
    }
}
