using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyEDA_Loader
{
    public class CanvasZoomPanHelper
    {
        private readonly Canvas _canvas;
        private readonly FrameworkElement _viewport;
        private readonly bool _fitCanvasExtent;
        private Point _lastDragPoint;
        private bool _isDragging;

        private readonly ScaleTransform _scaleTransform = new ScaleTransform();
        private readonly TranslateTransform _translateTransform = new TranslateTransform();
        private readonly TransformGroup _transformGroup = new TransformGroup();

        public CanvasZoomPanHelper(Canvas canvas) : this(canvas, null, false)
        {
        }

        // Supplying a viewport lets preview hosts use a clipped panel instead
        // of a ScrollViewer.  This is useful when the canvas has an explicit
        // drawing extent and must remain centered while still supporting pan
        // and wheel zoom.
        public CanvasZoomPanHelper(Canvas canvas, FrameworkElement viewport, bool fitCanvasExtent)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _viewport = viewport;
            _fitCanvasExtent = fitCanvasExtent;

            _transformGroup.Children.Add(_scaleTransform);
            _transformGroup.Children.Add(_translateTransform);

            _canvas.RenderTransform = _transformGroup;

            _canvas.Background = Brushes.Transparent;
            _canvas.Focusable = true;
            _canvas.Focus();

            AttachEvents();
        }

        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null && !(parent is T))
                parent = VisualTreeHelper.GetParent(parent);
            return parent as T;
        }

        private void AttachEvents()
        {
            FrameworkElement viewport = _viewport ?? (_canvas.Parent as ScrollViewer);
            if (viewport == null)
                throw new InvalidOperationException("Canvas must be inside a ScrollViewer or supplied with a viewport.");

            viewport.PreviewMouseWheel += Canvas_MouseWheel;
            viewport.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            viewport.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            viewport.PreviewMouseMove += Canvas_MouseMove;
            viewport.PreviewMouseRightButtonDown += Canvas_MouseRightButtonDown;
        }

        public void FitToBoundingBox()
        {
            if (_canvas == null || _canvas.Children.Count == 0)
                return;

            FrameworkElement viewport = _viewport ?? FindParent<ScrollViewer>(_canvas);
            if (viewport == null)
                return;

            Rect contentBounds = _fitCanvasExtent
                ? new Rect(0, 0, _canvas.Width, _canvas.Height)
                : CalculateCanvasBounds();
            if (contentBounds.IsEmpty || contentBounds.Width == 0 || contentBounds.Height == 0)
                return;

            double viewportWidth = _viewport != null ? _viewport.ActualWidth : ((ScrollViewer)viewport).ViewportWidth;
            double viewportHeight = _viewport != null ? _viewport.ActualHeight : ((ScrollViewer)viewport).ViewportHeight;

            if (viewportWidth <= 0 || viewportHeight <= 0)
                return;

            double scale = Math.Min(viewportWidth / contentBounds.Width, viewportHeight / contentBounds.Height);

            double centerOffsetX = (viewportWidth - contentBounds.Width * scale) / 2;
            double centerOffsetY = (viewportHeight - contentBounds.Height * scale) / 2;

            double translateX = -contentBounds.Left * scale + centerOffsetX;
            double translateY = -contentBounds.Top * scale + centerOffsetY;

            _scaleTransform.ScaleX = scale;
            _scaleTransform.ScaleY = scale;
            _translateTransform.X = translateX;
            _translateTransform.Y = translateY;
        }

        private Rect CalculateCanvasBounds()
        {
            Rect bounds = Rect.Empty;

            foreach (UIElement child in _canvas.Children)
            {
                Rect childRect = GetElementBounds(child);
                if (!childRect.IsEmpty)
                    bounds.Union(childRect);
            }

            return bounds;
        }

        private static Rect GetElementBounds(UIElement child)
        {
            if (child == null)
                return Rect.Empty;

            if (child is Line line)
            {
                Rect lineBounds = new Rect(
                    new Point(Math.Min(line.X1, line.X2), Math.Min(line.Y1, line.Y2)),
                    new Point(Math.Max(line.X1, line.X2), Math.Max(line.Y1, line.Y2)));
                InflateForStroke(lineBounds, line.StrokeThickness, out lineBounds);
                return OffsetForCanvas(line, lineBounds);
            }

            if (child is Shape shape)
            {
                Rect shapeBounds = shape.RenderedGeometry.Bounds;
                if (shapeBounds.IsEmpty || shapeBounds.Width == 0 && shapeBounds.Height == 0)
                    shapeBounds = GetFrameworkElementBounds(shape);

                InflateForStroke(shapeBounds, shape.StrokeThickness, out shapeBounds);
                return OffsetForCanvas(shape, shapeBounds);
            }

            if (child is FrameworkElement frameworkElement)
                return OffsetForCanvas(frameworkElement, GetFrameworkElementBounds(frameworkElement));

            Rect visualBounds = VisualTreeHelper.GetDescendantBounds(child);
            return OffsetForCanvas(child, visualBounds);
        }

        private static Rect GetFrameworkElementBounds(FrameworkElement element)
        {
            if (element == null)
                return Rect.Empty;

            double width = element.ActualWidth;
            double height = element.ActualHeight;

            if (width <= 0 || height <= 0)
            {
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (width <= 0)
                    width = element.DesiredSize.Width;
                if (height <= 0)
                    height = element.DesiredSize.Height;
            }

            if (width <= 0 && !double.IsNaN(element.Width))
                width = element.Width;
            if (height <= 0 && !double.IsNaN(element.Height))
                height = element.Height;

            if (width <= 0 && height <= 0)
                return Rect.Empty;

            return new Rect(0, 0, Math.Max(width, 0), Math.Max(height, 0));
        }

        private static Rect OffsetForCanvas(UIElement element, Rect bounds)
        {
            if (bounds.IsEmpty)
                return bounds;

            double left = Canvas.GetLeft(element);
            double top = Canvas.GetTop(element);

            if (!double.IsNaN(left))
                bounds.Offset(left, 0);
            if (!double.IsNaN(top))
                bounds.Offset(0, top);

            return bounds;
        }

        private static void InflateForStroke(Rect bounds, double strokeThickness, out Rect inflatedBounds)
        {
            inflatedBounds = bounds;
            if (inflatedBounds.IsEmpty)
                return;

            double padding = Math.Max(strokeThickness, 1.0) / 2.0;
            inflatedBounds.Inflate(padding, padding);
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is FrameworkElement))
                return;

            Point mousePos = e.GetPosition(_canvas);

            double zoomFactor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double newScale = _scaleTransform.ScaleX * zoomFactor;

            if (newScale < 0.01 || newScale > 100)
            {
                e.Handled = true;
                return;
            }

            Point transformedMouse = new Point(
                (mousePos.X * _scaleTransform.ScaleX) + _translateTransform.X,
                (mousePos.Y * _scaleTransform.ScaleY) + _translateTransform.Y
            );

            _scaleTransform.ScaleX = newScale;
            _scaleTransform.ScaleY = newScale;

            _translateTransform.X = transformedMouse.X - (mousePos.X * newScale);
            _translateTransform.Y = transformedMouse.Y - (mousePos.Y * newScale);

            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _lastDragPoint = e.GetPosition(sender as IInputElement);
            _isDragging = true;

            if (sender is UIElement element)
                element.CaptureMouse();

            Mouse.OverrideCursor = Cursors.SizeAll;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;

            if (sender is UIElement element)
                element.ReleaseMouseCapture();

            Mouse.OverrideCursor = null;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            if (sender is IInputElement element)
            {
                Point currentPos = e.GetPosition(element);
                Vector delta = currentPos - _lastDragPoint;
                _lastDragPoint = currentPos;

                _translateTransform.X += delta.X;
                _translateTransform.Y += delta.Y;
            }
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            FitToBoundingBox();
            e.Handled = true;
        }
    }
}
