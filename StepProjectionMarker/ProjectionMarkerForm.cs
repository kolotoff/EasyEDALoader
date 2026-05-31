using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace StepProjectionMarker
{
    internal sealed class ProjectionMarkerForm : Form
    {
        private readonly SplitContainer _splitContainer;
        private readonly ListBox _imageList;
        private readonly MarkerCanvas _canvas;
        private readonly Button _openButton;
        private readonly Button _previousButton;
        private readonly Button _nextButton;
        private readonly Button _saveButton;
        private readonly Label _statusLabel;
        private readonly Dictionary<string, AnnotationState> _states = new Dictionary<string, AnnotationState>(StringComparer.OrdinalIgnoreCase);

        private string _projectionDirectory;
        private string _markedDirectory;
        private List<string> _imagePaths = new List<string>();
        private string _currentImagePath;
        private Image _currentImage;

        public ProjectionMarkerForm(string projectionDirectory, string markedDirectory)
        {
            _projectionDirectory = projectionDirectory;
            _markedDirectory = markedDirectory;

            Text = "STEP Projection Marker";
            Width = 1280;
            Height = 860;
            MinimumSize = new Size(900, 600);
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(8, 7, 8, 7)
            };

            _openButton = new Button { Text = "Open", Width = 78, Height = 28 };
            _previousButton = new Button { Text = "Previous", Width = 86, Height = 28 };
            _nextButton = new Button { Text = "Next", Width = 70, Height = 28 };
            _saveButton = new Button { Text = "Save", Width = 70, Height = 28 };
            _statusLabel = new Label
            {
                AutoSize = false,
                Width = 820,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft
            };

            toolbar.Controls.Add(_openButton);
            toolbar.Controls.Add(_previousButton);
            toolbar.Controls.Add(_nextButton);
            toolbar.Controls.Add(_saveButton);
            toolbar.Controls.Add(_statusLabel);

            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterDistance = 320
            };

            _imageList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                HorizontalScrollbar = true
            };

            _canvas = new MarkerCanvas
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            _splitContainer.Panel1.Controls.Add(_imageList);
            _splitContainer.Panel2.Controls.Add(_canvas);

            Controls.Add(_splitContainer);
            Controls.Add(toolbar);

            _openButton.Click += OpenButton_Click;
            _previousButton.Click += PreviousButton_Click;
            _nextButton.Click += NextButton_Click;
            _saveButton.Click += SaveButton_Click;
            _imageList.SelectedIndexChanged += ImageList_SelectedIndexChanged;
            _canvas.RectangleCreated += Canvas_RectangleCreated;

            LoadProjectionDirectory(_projectionDirectory, _markedDirectory);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (HasDirtyState())
            {
                DialogResult result = MessageBox.Show(
                    this,
                    "Save rectangle JSON files before closing?",
                    "Unsaved Markers",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == DialogResult.Yes)
                    SaveAll();
            }

            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                SaveAll();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.Z)
            {
                UndoCurrent();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.Y)
            {
                RedoCurrent();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Left)
            {
                SelectRelativeImage(-1);
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Right)
            {
                SelectRelativeImage(1);
                e.SuppressKeyPress = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void OpenButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Projection folder";
                dialog.SelectedPath = Directory.Exists(_projectionDirectory) ? _projectionDirectory : Directory.GetCurrentDirectory();

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string projectionDirectory = dialog.SelectedPath;
                string markedDirectory = GetDefaultMarkedDirectory(projectionDirectory);
                LoadProjectionDirectory(projectionDirectory, markedDirectory);
            }
        }

        private void PreviousButton_Click(object sender, EventArgs e)
        {
            SelectRelativeImage(-1);
        }

        private void NextButton_Click(object sender, EventArgs e)
        {
            SelectRelativeImage(1);
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            SaveAll();
        }

        private void ImageList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_imageList.SelectedIndex < 0 || _imageList.SelectedIndex >= _imagePaths.Count)
                return;

            ShowImage(_imagePaths[_imageList.SelectedIndex]);
        }

        private void Canvas_RectangleCreated(object sender, MarkerRectangle rectangle)
        {
            AnnotationState state = GetCurrentState();
            if (state == null)
                return;

            state.Rectangles.Add(rectangle);
            state.Undo.Push(new MarkerAction(MarkerActionKind.Add, rectangle));
            state.Redo.Clear();
            state.Dirty = true;
            UpdateCanvasState();
            UpdateStatus();
        }

        private void LoadProjectionDirectory(string projectionDirectory, string markedDirectory)
        {
            _currentImagePath = null;
            DisposeCurrentImage();
            _states.Clear();
            _imageList.Items.Clear();
            _canvas.SetImage(null);

            _projectionDirectory = Path.GetFullPath(projectionDirectory);
            _markedDirectory = Path.GetFullPath(markedDirectory);

            if (!Directory.Exists(_projectionDirectory))
            {
                UpdateStatus("Projection folder not found: " + _projectionDirectory);
                return;
            }

            Directory.CreateDirectory(_markedDirectory);

            _imagePaths = Directory.GetFiles(_projectionDirectory, "*.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string imagePath in _imagePaths)
                _imageList.Items.Add(Path.GetFileName(imagePath));

            if (_imagePaths.Count > 0)
                _imageList.SelectedIndex = 0;
            else
                UpdateStatus("No PNG projection images found in: " + _projectionDirectory);
        }

        private void ShowImage(string imagePath)
        {
            _currentImagePath = imagePath;
            DisposeCurrentImage();
            _currentImage = LoadImageWithoutLock(imagePath);

            AnnotationState state = GetOrLoadState(imagePath, _currentImage.Width, _currentImage.Height);
            _canvas.SetImage(_currentImage);
            _canvas.SetRectangles(state.Rectangles);
            UpdateStatus();
        }

        private AnnotationState GetOrLoadState(string imagePath, int imageWidth, int imageHeight)
        {
            if (_states.TryGetValue(imagePath, out AnnotationState existing))
                return existing;

            var state = new AnnotationState();
            string jsonPath = GetJsonPath(imagePath);
            if (File.Exists(jsonPath))
            {
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    MarkerDocument document = JsonSerializer.Deserialize<MarkerDocument>(json);
                    if (document != null && document.Rectangles != null)
                        state.Rectangles.AddRange(document.Rectangles.Where(r => r.Width > 0 && r.Height > 0));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Could not read marker JSON:\r\n" + jsonPath + "\r\n\r\n" + ex.Message,
                        "Marker JSON",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            _states[imagePath] = state;
            return state;
        }

        private AnnotationState GetCurrentState()
        {
            if (string.IsNullOrEmpty(_currentImagePath))
                return null;

            if (_states.TryGetValue(_currentImagePath, out AnnotationState state))
                return state;

            return null;
        }

        private void UndoCurrent()
        {
            AnnotationState state = GetCurrentState();
            if (state == null || state.Undo.Count == 0)
                return;

            MarkerAction action = state.Undo.Pop();
            ApplyReverseAction(state, action);
            state.Redo.Push(action);
            state.Dirty = true;
            UpdateCanvasState();
            UpdateStatus();
        }

        private void RedoCurrent()
        {
            AnnotationState state = GetCurrentState();
            if (state == null || state.Redo.Count == 0)
                return;

            MarkerAction action = state.Redo.Pop();
            ApplyAction(state, action);
            state.Undo.Push(action);
            state.Dirty = true;
            UpdateCanvasState();
            UpdateStatus();
        }

        private void ApplyAction(AnnotationState state, MarkerAction action)
        {
            if (action.Kind == MarkerActionKind.Add)
                state.Rectangles.Add(action.Rectangle);
        }

        private void ApplyReverseAction(AnnotationState state, MarkerAction action)
        {
            if (action.Kind != MarkerActionKind.Add)
                return;

            for (int i = state.Rectangles.Count - 1; i >= 0; i--)
            {
                if (state.Rectangles[i].Equals(action.Rectangle))
                {
                    state.Rectangles.RemoveAt(i);
                    return;
                }
            }
        }

        private void UpdateCanvasState()
        {
            AnnotationState state = GetCurrentState();
            _canvas.SetRectangles(state?.Rectangles ?? new List<MarkerRectangle>());
        }

        private void SelectRelativeImage(int offset)
        {
            if (_imageList.Items.Count == 0)
                return;

            int selected = _imageList.SelectedIndex < 0 ? 0 : _imageList.SelectedIndex;
            int next = Math.Max(0, Math.Min(_imageList.Items.Count - 1, selected + offset));
            _imageList.SelectedIndex = next;
        }

        private void SaveAll()
        {
            if (_states.Count == 0)
                return;

            Directory.CreateDirectory(_markedDirectory);
            int savedCount = 0;
            foreach (KeyValuePair<string, AnnotationState> pair in _states)
            {
                string imagePath = pair.Key;
                AnnotationState state = pair.Value;
                string jsonPath = GetJsonPath(imagePath);

                if (!state.Dirty && state.Rectangles.Count == 0 && !File.Exists(jsonPath))
                    continue;

                Size imageSize = GetImageSize(imagePath);
                var document = new MarkerDocument
                {
                    Version = 1,
                    ImageFile = Path.GetFileName(imagePath),
                    ImageWidth = imageSize.Width,
                    ImageHeight = imageSize.Height,
                    Rectangles = state.Rectangles.ToList()
                };

                string json = JsonSerializer.Serialize(document, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                File.WriteAllText(jsonPath, json);
                state.Dirty = false;
                savedCount++;
            }

            UpdateStatus("Saved " + savedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " marker JSON file(s).");
        }

        private Size GetImageSize(string imagePath)
        {
            if (string.Equals(imagePath, _currentImagePath, StringComparison.OrdinalIgnoreCase) && _currentImage != null)
                return _currentImage.Size;

            using (Image image = LoadImageWithoutLock(imagePath))
                return image.Size;
        }

        private bool HasDirtyState()
        {
            return _states.Values.Any(state => state.Dirty);
        }

        private string GetJsonPath(string imagePath)
        {
            return Path.Combine(_markedDirectory, Path.GetFileNameWithoutExtension(imagePath) + ".json");
        }

        private static string GetDefaultMarkedDirectory(string projectionDirectory)
        {
            string fullProjectionDirectory = Path.GetFullPath(projectionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string parent = Directory.GetParent(fullProjectionDirectory)?.FullName;
            if (!string.IsNullOrEmpty(parent) &&
                string.Equals(Path.GetFileName(fullProjectionDirectory), "Projection", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(parent, "Marked");

            return Path.Combine(fullProjectionDirectory, "Marked");
        }

        private static Image LoadImageWithoutLock(string imagePath)
        {
            using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        private void UpdateStatus(string message = null)
        {
            if (!string.IsNullOrEmpty(message))
            {
                _statusLabel.Text = message;
                return;
            }

            if (string.IsNullOrEmpty(_currentImagePath))
            {
                _statusLabel.Text = "Projection: " + _projectionDirectory;
                return;
            }

            AnnotationState state = GetCurrentState();
            int rectangleCount = state?.Rectangles.Count ?? 0;
            string dirty = state != null && state.Dirty ? " *" : string.Empty;
            _statusLabel.Text =
                Path.GetFileName(_currentImagePath) +
                dirty +
                " | rectangles: " + rectangleCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " | output: " + _markedDirectory;
        }

        private void DisposeCurrentImage()
        {
            if (_currentImage != null)
            {
                _currentImage.Dispose();
                _currentImage = null;
            }
        }
    }

    internal sealed class MarkerCanvas : Control
    {
        private readonly List<MarkerRectangle> _rectangles = new List<MarkerRectangle>();
        private Image _image;
        private Point? _dragStart;
        private Point? _dragCurrent;

        public event EventHandler<MarkerRectangle> RectangleCreated;

        public MarkerCanvas()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Cross;
        }

        public void SetImage(Image image)
        {
            _image = image;
            _dragStart = null;
            _dragCurrent = null;
            Invalidate();
        }

        public void SetRectangles(IReadOnlyList<MarkerRectangle> rectangles)
        {
            _rectangles.Clear();
            _rectangles.AddRange(rectangles);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.Clear(Color.White);
            if (_image == null)
                return;

            RectangleF imageView = GetImageView();
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_image, imageView);

            using (var pen = new Pen(Color.Red, 3.0f))
            {
                foreach (MarkerRectangle rectangle in _rectangles)
                    e.Graphics.DrawRectangle(pen, ToViewRectangle(rectangle, imageView));
            }

            if (_dragStart.HasValue && _dragCurrent.HasValue)
            {
                using (var pen = new Pen(Color.Red, 2.0f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    Rectangle marker = NormalizeRectangle(_dragStart.Value, _dragCurrent.Value);
                    e.Graphics.DrawRectangle(pen, marker);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _image == null || !GetImageView().Contains(e.Location))
                return;

            Focus();
            _dragStart = e.Location;
            _dragCurrent = e.Location;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragStart.HasValue)
                return;

            _dragCurrent = ClampToImageView(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragStart.HasValue || _image == null)
                return;

            Point end = ClampToImageView(e.Location);
            Point start = _dragStart.Value;
            _dragStart = null;
            _dragCurrent = null;
            Capture = false;

            MarkerRectangle rectangle = ToImageRectangle(NormalizeRectangle(start, end));
            if (rectangle.Width >= 3 && rectangle.Height >= 3)
                RectangleCreated?.Invoke(this, rectangle);

            Invalidate();
        }

        private RectangleF GetImageView()
        {
            if (_image == null || Width <= 0 || Height <= 0)
                return RectangleF.Empty;

            const int margin = 16;
            float availableWidth = Math.Max(1, Width - margin * 2);
            float availableHeight = Math.Max(1, Height - margin * 2);
            float scale = Math.Min(availableWidth / _image.Width, availableHeight / _image.Height);
            float drawWidth = _image.Width * scale;
            float drawHeight = _image.Height * scale;
            float x = (Width - drawWidth) / 2.0f;
            float y = (Height - drawHeight) / 2.0f;
            return new RectangleF(x, y, drawWidth, drawHeight);
        }

        private Rectangle ToViewRectangle(MarkerRectangle rectangle, RectangleF imageView)
        {
            float scaleX = imageView.Width / _image.Width;
            float scaleY = imageView.Height / _image.Height;

            return Rectangle.Round(new RectangleF(
                imageView.X + rectangle.X * scaleX,
                imageView.Y + rectangle.Y * scaleY,
                rectangle.Width * scaleX,
                rectangle.Height * scaleY));
        }

        private MarkerRectangle ToImageRectangle(Rectangle viewRectangle)
        {
            RectangleF imageView = GetImageView();
            double scaleX = _image.Width / imageView.Width;
            double scaleY = _image.Height / imageView.Height;

            int x = Clamp((int)Math.Round((viewRectangle.X - imageView.X) * scaleX), 0, _image.Width);
            int y = Clamp((int)Math.Round((viewRectangle.Y - imageView.Y) * scaleY), 0, _image.Height);
            int right = Clamp((int)Math.Round((viewRectangle.Right - imageView.X) * scaleX), 0, _image.Width);
            int bottom = Clamp((int)Math.Round((viewRectangle.Bottom - imageView.Y) * scaleY), 0, _image.Height);

            return new MarkerRectangle
            {
                X = Math.Min(x, right),
                Y = Math.Min(y, bottom),
                Width = Math.Abs(right - x),
                Height = Math.Abs(bottom - y)
            };
        }

        private Point ClampToImageView(Point point)
        {
            RectangleF imageView = GetImageView();
            int x = Clamp(point.X, (int)Math.Floor(imageView.Left), (int)Math.Ceiling(imageView.Right));
            int y = Clamp(point.Y, (int)Math.Floor(imageView.Top), (int)Math.Ceiling(imageView.Bottom));
            return new Point(x, y);
        }

        private static Rectangle NormalizeRectangle(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            int width = Math.Abs(a.X - b.X);
            int height = Math.Abs(a.Y - b.Y);
            return new Rectangle(x, y, width, height);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }
    }

    internal sealed class AnnotationState
    {
        public List<MarkerRectangle> Rectangles { get; } = new List<MarkerRectangle>();
        public Stack<MarkerAction> Undo { get; } = new Stack<MarkerAction>();
        public Stack<MarkerAction> Redo { get; } = new Stack<MarkerAction>();
        public bool Dirty { get; set; }
    }

    internal enum MarkerActionKind
    {
        Add
    }

    internal sealed class MarkerAction
    {
        public MarkerAction(MarkerActionKind kind, MarkerRectangle rectangle)
        {
            Kind = kind;
            Rectangle = rectangle;
        }

        public MarkerActionKind Kind { get; }
        public MarkerRectangle Rectangle { get; }
    }

    internal sealed class MarkerDocument
    {
        public int Version { get; set; }
        public string ImageFile { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public List<MarkerRectangle> Rectangles { get; set; } = new List<MarkerRectangle>();
    }

    internal sealed class MarkerRectangle : IEquatable<MarkerRectangle>
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public bool Equals(MarkerRectangle other)
        {
            if (other == null)
                return false;

            return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as MarkerRectangle);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Width;
                hash = (hash * 397) ^ Height;
                return hash;
            }
        }
    }
}
