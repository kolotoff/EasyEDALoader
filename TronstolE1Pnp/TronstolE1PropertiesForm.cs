using System.Drawing;
using System.Windows.Forms;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    internal sealed class TronstolE1PropertiesForm : Form
    {
        private readonly CheckBox removeBgaSuffixCheckBox;
        private readonly CheckBox removeSpaceBgaSuffixCheckBox;
        private readonly CheckBox skipNfComponentsCheckBox;
        private readonly CheckBox skipDnpComponentsCheckBox;
        private readonly CheckBox skipManualSolderingComponentsCheckBox;
        private readonly CheckBox skipWaveSolderingComponentsCheckBox;
        private readonly CheckBox skipTestPointsCheckBox;
        private readonly CheckBox skipSolderBridgeCheckBox;
        private readonly CheckBox exportPanelFiducialsCheckBox;
        private readonly CheckBox exportBoardDimensionsCheckBox;
        private readonly CheckBox exportEdgeRailsSizeCheckBox;
        private readonly CheckBox removeFootprintFromPartNumberCheckBox;

        public bool RemoveBgaSuffix => removeBgaSuffixCheckBox.Checked;
        public bool RemoveSpaceBgaSuffix => removeSpaceBgaSuffixCheckBox.Checked;
        public bool SkipNfComponents => skipNfComponentsCheckBox.Checked;
        public bool SkipDnpComponents => skipDnpComponentsCheckBox.Checked;
        public bool SkipManualSolderingComponents => skipManualSolderingComponentsCheckBox.Checked;
        public bool SkipWaveSolderingComponents => skipWaveSolderingComponentsCheckBox.Checked;
        public bool SkipTestPoints => skipTestPointsCheckBox.Checked;
        public bool SkipSolderBridge => skipSolderBridgeCheckBox.Checked;
        public bool ExportPanelFiducials => exportPanelFiducialsCheckBox.Checked;
        public bool ExportBoardDimensions => exportBoardDimensionsCheckBox.Checked;
        public bool ExportEdgeRailsSize => exportEdgeRailsSizeCheckBox.Checked;
        public bool RemoveFootprintFromPartNumber => removeFootprintFromPartNumberCheckBox.Checked;

        public TronstolE1PropertiesForm(
            bool removeBgaSuffix,
            bool removeSpaceBgaSuffix,
            bool skipNfComponents,
            bool skipDnpComponents,
            bool skipManualSolderingComponents,
            bool skipWaveSolderingComponents,
            bool skipTestPoints,
            bool skipSolderBridge,
            bool exportPanelFiducials,
            bool exportBoardDimensions,
            bool exportEdgeRailsSize,
            bool removeFootprintFromPartNumber)
        {
            Text = "Tronstol E1 PNP";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(576, 465);

            removeBgaSuffixCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = removeBgaSuffix,
                Location = new Point(16, 18),
                Text = "Remove _BGA suffix from footprint name"
            };

            removeSpaceBgaSuffixCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = removeSpaceBgaSuffix,
                Location = new Point(16, 48),
                Text = "Remove \" BGA\" suffix from footprint name"
            };

            skipNfComponentsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = skipNfComponents,
                Location = new Point(16, 78),
                Text = "Skip NF components"
            };

            skipDnpComponentsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = skipDnpComponents,
                Location = new Point(16, 108),
                Text = "Skip DNP components"
            };

            skipManualSolderingComponentsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = skipManualSolderingComponents,
                Location = new Point(16, 138),
                Text = "Skip manual soldering components"
            };

            skipWaveSolderingComponentsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = skipWaveSolderingComponents,
                Location = new Point(16, 168),
                Text = "Skip Wave soldering components"
            };

            skipTestPointsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = skipTestPoints,
                Location = new Point(16, 198),
                Text = "Skip test points"
            };

            skipSolderBridgeCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = skipSolderBridge,
                Location = new Point(16, 228),
                Text = "Skip solder bridge"
            };

            exportPanelFiducialsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = exportPanelFiducials,
                Location = new Point(16, 258),
                Text = "Export panel fiducials"
            };

            exportBoardDimensionsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = exportBoardDimensions,
                Location = new Point(16, 288),
                Text = "Export board dimensions"
            };

            exportEdgeRailsSizeCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = exportEdgeRailsSize,
                Location = new Point(16, 318),
                Text = "Export edge rails size"
            };

            removeFootprintFromPartNumberCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = removeFootprintFromPartNumber,
                Location = new Point(16, 348),
                Text = "Remove footprint name from end of PartNumber"
            };

            var okButton = new Button
            {
                DialogResult = DialogResult.OK,
                Location = new Point(404, 418),
                Size = new Size(75, 31),
                Text = "OK"
            };

            var cancelButton = new Button
            {
                DialogResult = DialogResult.Cancel,
                Location = new Point(485, 418),
                Size = new Size(75, 31),
                Text = "Cancel"
            };

            AcceptButton = okButton;
            CancelButton = cancelButton;
            Controls.Add(removeBgaSuffixCheckBox);
            Controls.Add(removeSpaceBgaSuffixCheckBox);
            Controls.Add(skipNfComponentsCheckBox);
            Controls.Add(skipDnpComponentsCheckBox);
            Controls.Add(skipManualSolderingComponentsCheckBox);
            Controls.Add(skipWaveSolderingComponentsCheckBox);
            Controls.Add(skipTestPointsCheckBox);
            Controls.Add(skipSolderBridgeCheckBox);
            Controls.Add(exportPanelFiducialsCheckBox);
            Controls.Add(exportBoardDimensionsCheckBox);
            Controls.Add(exportEdgeRailsSizeCheckBox);
            Controls.Add(removeFootprintFromPartNumberCheckBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
        }
    }
}
