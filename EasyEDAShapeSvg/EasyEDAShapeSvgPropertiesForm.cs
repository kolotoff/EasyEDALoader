using System.Drawing;
using System.Windows.Forms;

namespace EasyEDA_Loader.EasyEDAShapeSvg
{
    internal sealed class EasyEDAShapeSvgPropertiesForm : Form
    {
        private readonly CheckBox includePadsCheckBox;

        public bool IncludePads => includePadsCheckBox.Checked;

        public EasyEDAShapeSvgPropertiesForm(bool includePads)
        {
            Text = "EasyEDA Shape SVG Export";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 142);

            var targetFolderLabel = new Label
            {
                AutoSize = true,
                Location = new Point(16, 18),
                Text = "SVG target folder is set by the OutJob output path."
            };

            includePadsCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = includePads,
                Location = new Point(16, 54),
                Text = "Export component pads"
            };

            var okButton = new Button
            {
                DialogResult = DialogResult.OK,
                Location = new Point(300, 96),
                Size = new Size(68, 28),
                Text = "OK"
            };

            var cancelButton = new Button
            {
                DialogResult = DialogResult.Cancel,
                Location = new Point(376, 96),
                Size = new Size(68, 28),
                Text = "Cancel"
            };

            AcceptButton = okButton;
            CancelButton = cancelButton;
            Controls.Add(targetFolderLabel);
            Controls.Add(includePadsCheckBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
        }
    }
}
