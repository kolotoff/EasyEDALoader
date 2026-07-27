using System;
using System.Drawing;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    internal sealed class ShapeExportErrorForm : Form
    {
        private readonly TextBox errorTextBox;
        private readonly Button copyButton;

        public ShapeExportErrorForm(string errorText)
        {
            Text = "EasyEDA Loader - Shape Export Errors";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(560, 366);
            ClientSize = new Size(760, 506);

            var summaryLabel = new Label
            {
                AutoSize = false,
                Location = new Point(12, 12),
                Size = new Size(736, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Shape export completed with errors. The remaining footprints and libraries were processed."
            };

            errorTextBox = new TextBox
            {
                Location = new Point(12, 52),
                Size = new Size(736, 396),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = SystemColors.Window,
                Font = new Font(FontFamily.GenericMonospace, 9.0f),
                Text = errorText ?? ""
            };

            copyButton = new Button
            {
                Location = new Point(436, 460),
                Size = new Size(200, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Copy to clipboard"
            };
            copyButton.Click += CopyButtonClick;

            var closeButton = new Button
            {
                Location = new Point(644, 460),
                Size = new Size(104, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
                Text = "Close"
            };

            AcceptButton = closeButton;
            CancelButton = closeButton;
            Controls.Add(summaryLabel);
            Controls.Add(errorTextBox);
            Controls.Add(copyButton);
            Controls.Add(closeButton);

            Shown += (_, _) =>
            {
                BringToFront();
                Activate();
                errorTextBox.SelectionStart = 0;
                errorTextBox.SelectionLength = 0;
            };
        }

        private void CopyButtonClick(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(errorTextBox.Text);
                copyButton.Text = "Copied";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not copy the error list to the clipboard:" + Environment.NewLine + ex.Message,
                    "EasyEDA Loader Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
