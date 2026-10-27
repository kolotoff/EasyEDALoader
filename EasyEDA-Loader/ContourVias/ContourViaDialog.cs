using PCB;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyEDA_Loader
{
    internal sealed class ContourViaDialog : Window
    {
        private readonly IPCB_Board board;
        private readonly TextBox diameter = Input("ViaDiameter", "0.4");
        private readonly TextBox hole = Input("DrillDiameter", "0.2");
        private readonly TextBox clearance = Input("KeepoutClearance", "0.3");
        private readonly TextBox pitch = Input("ViaPitch", "2.0");
        private readonly ComboBox net = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 4, 0, 4) };
        private readonly CheckBox ignoreObstacles = new CheckBox { Content = "Ignore obstacles", IsChecked = false, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 12, 0, 0) };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
        private readonly Button add = new Button { Content = "Add vias", IsDefault = true, MinWidth = 95, Padding = new Thickness(12, 5, 12, 5) };

        public ContourViaDialog(IPCB_Board board)
        {
            this.board = board;
            Title = "Add Tented Vias Around Keepout";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI");
            Background = new SolidColorBrush(Color.FromRgb(62, 62, 62));
            Foreground = Brushes.WhiteSmoke;
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Place through vias along Keep-Out Layer tracks and arcs.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddRow(grid, "Via outer diameter (mm)", diameter);
            AddRow(grid, "Drill diameter (mm)", hole);
            AddRow(grid, "Keepout clearance to via edge (mm)", clearance);
            AddRow(grid, "Via pitch (mm)", pitch);
            AutomationProperties.SetAutomationId(net, "TargetNet");
            net.Items.Add("(No net)");
            foreach (string name in ContourViaPcbWriter.ReadNetNames(board)) net.Items.Add(name);
            net.SelectedIndex = 0;
            AddRow(grid, "Target net", net);
            panel.Children.Add(grid);
            AutomationProperties.SetAutomationId(ignoreObstacles, "IgnoreObstacles");
            ignoreObstacles.ToolTip = "Place all generated vias, even if they touch or violate clearance to other nets.";
            panel.Children.Add(ignoreObstacles);
            panel.Children.Add(new TextBlock { Text = "Vias are fully tented on both sides. Centre offset = clearance + half the via diameter.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            panel.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 85, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
            AutomationProperties.SetAutomationId(add, "AddContourVias");
            AutomationProperties.SetAutomationId(cancel, "CancelContourVias");
            buttons.Children.Add(add); buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
            foreach (var input in new[] { diameter, hole, clearance, pitch }) input.TextChanged += (_, __) => ValidateInputs();
            net.SelectionChanged += (_, __) => ValidateInputs();
            ignoreObstacles.Checked += (_, __) => ValidateInputs();
            ignoreObstacles.Unchecked += (_, __) => ValidateInputs();
            add.Click += Add_Click;
            ValidateInputs();
        }

        private static TextBox Input(string id, string value)
        {
            var box = new TextBox { Text = value, MinWidth = 180, Padding = new Thickness(5), Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetAutomationId(box, id);
            return box;
        }

        private static void AddRow(Grid grid, string caption, Control input)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new Label { Content = caption, Target = input, Foreground = Brushes.WhiteSmoke, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(label, row); Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            grid.Children.Add(label); grid.Children.Add(input);
        }

        private ContourViaOptions ReadOptions()
        {
            var options = new ContourViaOptions
            {
                DiameterMm = ReadMm(diameter, "Via diameter"), HoleMm = ReadMm(hole, "Drill diameter"),
                ClearanceMm = ReadMm(clearance, "Keepout clearance"), PitchMm = ReadMm(pitch, "Pitch"),
                NetName = net.SelectedIndex <= 0 ? "" : (string)net.SelectedItem,
                IgnoreObstacles = ignoreObstacles.IsChecked == true
            };
            options.Validate();
            return options;
        }

        private static double ReadMm(TextBox input, string label)
        {
            if ((!double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
                && !double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) || !double.IsFinite(value))
                throw new ArgumentException(label + " must be a finite number in mm.");
            return value;
        }

        private void ValidateInputs()
        {
            try
            {
                var options = ReadOptions();
                int count = ContourViaPcbWriter.Plan(board, options).Count;
                status.Text = "Ready to add " + count + " vias. "
                    + (options.IgnoreObstacles ? "Obstacles are ignored. " : "Positions blocked by other nets are skipped. ")
                    + "The PCB will remain unsaved.";
                status.Foreground = Brushes.WhiteSmoke;
                add.IsEnabled = count > 0;
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                status.Foreground = Brushes.LightSalmon;
                add.IsEnabled = false;
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ContourViaPcbWriter.Add(board, ReadOptions());
                DialogResult = true;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Contour vias failed: " + ex);
                status.Text = ex.Message;
                status.Foreground = Brushes.LightSalmon;
            }
        }
    }
}
