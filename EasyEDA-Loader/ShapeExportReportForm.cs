using System;
using System.Drawing;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    public sealed class ShapeExportReportForm : Form
    {
        private readonly TextBox reportTextBox;
        private readonly Button copyButton;

        public ShapeExportReportForm(string title, string summary, string report)
        {
            Text = title ?? "SVG Shapes report";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(640, 400);
            ClientSize = new Size(980, 620);

            var summaryLabel = new Label
            {
                AutoEllipsis = true,
                Location = new Point(12, 12),
                Size = new Size(944, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = summary ?? "SVG Shapes export report"
            };

            reportTextBox = new TextBox
            {
                Location = new Point(12, 54),
                Size = new Size(944, 506),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = SystemColors.Window,
                Font = new Font(FontFamily.GenericMonospace, 9.0f),
                Text = report ?? ""
            };

            copyButton = new Button
            {
                Location = new Point(636, 572),
                Size = new Size(200, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Text = "Copy to clipboard"
            };
            copyButton.Click += CopyButtonClick;

            var closeButton = new Button
            {
                Location = new Point(844, 572),
                Size = new Size(92, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
                Text = "Close"
            };

            AcceptButton = closeButton;
            CancelButton = closeButton;
            Controls.Add(summaryLabel);
            Controls.Add(reportTextBox);
            Controls.Add(copyButton);
            Controls.Add(closeButton);

            Shown += (_, _) =>
            {
                BringToFront();
                Activate();
                reportTextBox.SelectionStart = 0;
                reportTextBox.SelectionLength = 0;
            };
        }

        private void CopyButtonClick(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(reportTextBox.Text);
                copyButton.Text = "Copied";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not copy the report to the clipboard:" + Environment.NewLine + ex.Message,
                    "SVG Shapes",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
