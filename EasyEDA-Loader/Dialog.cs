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

        public List<ComponentSelection> SelectedComponents => wpfDialog?.SelectedComponents;
        public bool RemoveWatermark => wpfDialog?.RemoveWatermark ?? true;

        public Dialog()
        {
            wpfDialog = new DialogWindow();
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

    public enum ComponentImportTarget
    {
        TemporaryLibraries,
        ActivePcbLibrary,
        ActiveSchLibrary
    }
}
