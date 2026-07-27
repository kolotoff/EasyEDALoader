using System;
using System.Drawing;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    internal sealed class ShapeExportProgressForm : Form
    {
        private readonly ProgressBar progressBar;
        private readonly Label statusLabel;
        private readonly Label detailLabel;
        private readonly Button cancelButton;

        public ShapeExportProgressForm()
        {
            Text = "Export shape";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ClientSize = new Size(420, 142);

            statusLabel = new Label
            {
                AutoEllipsis = true,
                Location = new Point(12, 12),
                Size = new Size(396, 20),
                Text = "Preparing export..."
            };

            progressBar = new ProgressBar
            {
                Location = new Point(12, 40),
                Size = new Size(396, 16),
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Marquee
            };

            detailLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
                Location = new Point(12, 66),
                Size = new Size(396, 20),
                Text = ""
            };

            cancelButton = new Button
            {
                Location = new Point(316, 98),
                Size = new Size(92, 32),
                Text = "Cancel"
            };
            cancelButton.Click += (_, _) =>
            {
                IsCancellationRequested = true;
                cancelButton.Enabled = false;
                statusLabel.Text = "Cancelling export...";
                Refresh();
            };

            Controls.Add(statusLabel);
            Controls.Add(progressBar);
            Controls.Add(detailLabel);
            Controls.Add(cancelButton);
        }

        public bool IsCancellationRequested { get; private set; }

        public void Report(ShapeExportProgress progress)
        {
            if (IsDisposed || progress == null)
                return;

            statusLabel.Text = string.IsNullOrWhiteSpace(progress.Message)
                ? "Exporting shapes..."
                : progress.Message;
            detailLabel.Text = progress.Detail ?? "";

            if (progress.Percent.HasValue)
            {
                progressBar.Style = ProgressBarStyle.Continuous;
                int value = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, (int)Math.Round(progress.Percent.Value)));
                progressBar.Value = value;
            }
            else
            {
                progressBar.Style = ProgressBarStyle.Marquee;
            }

            if (!Visible)
                Show();
            BringToFront();
            Activate();
            Refresh();
            Application.DoEvents();
        }
    }
}
