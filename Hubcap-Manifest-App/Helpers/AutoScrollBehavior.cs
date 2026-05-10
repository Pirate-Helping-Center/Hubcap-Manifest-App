using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HubcapManifestApp.Helpers
{
    /// <summary>
    /// Attached behavior that adds browser-style middle-click auto-scroll to any ScrollViewer.
    /// Middle-click to start, move mouse to scroll, click again to stop.
    /// </summary>
    public static class AutoScrollBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(AutoScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        private static ScrollViewer? _activeScrollViewer;
        private static Point _origin;
        private static Ellipse? _indicator;
        private static DispatcherTimer? _scrollTimer;
        private static bool _isAutoScrolling;

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                if ((bool)e.NewValue)
                {
                    sv.PreviewMouseDown += OnPreviewMouseDown;
                    sv.PreviewMouseUp += OnPreviewMouseUp;
                    sv.PreviewMouseMove += OnPreviewMouseMove;
                }
                else
                {
                    sv.PreviewMouseDown -= OnPreviewMouseDown;
                    sv.PreviewMouseUp -= OnPreviewMouseUp;
                    sv.PreviewMouseMove -= OnPreviewMouseMove;
                    StopAutoScroll();
                }
            }
        }

        private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && sender is ScrollViewer sv)
            {
                if (_isAutoScrolling)
                {
                    StopAutoScroll();
                    e.Handled = true;
                    return;
                }

                _activeScrollViewer = sv;
                _origin = e.GetPosition(sv);
                _isAutoScrolling = true;

                // Show indicator
                var adornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(sv);
                if (adornerLayer == null)
                {
                    // Fallback: add indicator as overlay
                    ShowIndicator(sv);
                }
                else
                {
                    ShowIndicator(sv);
                }

                // Start scroll timer
                _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _scrollTimer.Tick += ScrollTimer_Tick;
                _scrollTimer.Start();

                sv.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            }
            else if (_isAutoScrolling && e.ChangedButton == MouseButton.Left)
            {
                StopAutoScroll();
            }
        }

        private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // Don't stop on middle-up — Firefox keeps scrolling until next click
        }

        private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            // Nothing needed — timer reads mouse position
        }

        private static void ScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (_activeScrollViewer == null || !_isAutoScrolling) return;

            var currentPos = Mouse.GetPosition(_activeScrollViewer);
            var deltaY = currentPos.Y - _origin.Y;
            var deltaX = currentPos.X - _origin.X;

            // Dead zone of 10px
            const double deadZone = 10;

            if (Math.Abs(deltaY) > deadZone)
            {
                // Speed scales with distance from origin
                var speed = (deltaY - Math.Sign(deltaY) * deadZone) * 0.15;
                _activeScrollViewer.ScrollToVerticalOffset(_activeScrollViewer.VerticalOffset + speed);
            }

            if (Math.Abs(deltaX) > deadZone)
            {
                var speed = (deltaX - Math.Sign(deltaX) * deadZone) * 0.15;
                _activeScrollViewer.ScrollToHorizontalOffset(_activeScrollViewer.HorizontalOffset + speed);
            }

            // Update indicator cursor direction
            UpdateCursor(deltaX, deltaY, deadZone);
        }

        private static void UpdateCursor(double dx, double dy, double deadZone)
        {
            if (_activeScrollViewer == null) return;

            if (Math.Abs(dy) <= deadZone && Math.Abs(dx) <= deadZone)
                _activeScrollViewer.Cursor = Cursors.ScrollAll;
            else if (Math.Abs(dy) > Math.Abs(dx))
                _activeScrollViewer.Cursor = dy < 0 ? Cursors.ScrollN : Cursors.ScrollS;
            else
                _activeScrollViewer.Cursor = dx < 0 ? Cursors.ScrollW : Cursors.ScrollE;
        }

        private static void ShowIndicator(ScrollViewer sv)
        {
            _indicator = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = new SolidColorBrush(Color.FromArgb(160, 128, 128, 128)),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 180, 180, 180)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };

            // Place it on the ScrollViewer's content panel
            if (sv.Content is Panel panel)
            {
                Canvas.SetLeft(_indicator, _origin.X - 10 + sv.HorizontalOffset);
                Canvas.SetTop(_indicator, _origin.Y - 10 + sv.VerticalOffset);
                // Can't add to arbitrary panel easily, use a different approach
            }

            // Use a popup-style overlay via the window
            var window = Window.GetWindow(sv);
            if (window?.Content is Grid rootGrid)
            {
                var pos = sv.TranslatePoint(_origin, rootGrid);
                _indicator.Margin = new Thickness(pos.X - 10, pos.Y - 10, 0, 0);
                _indicator.HorizontalAlignment = HorizontalAlignment.Left;
                _indicator.VerticalAlignment = VerticalAlignment.Top;
                rootGrid.Children.Add(_indicator);
            }
        }

        private static void StopAutoScroll()
        {
            _isAutoScrolling = false;

            _scrollTimer?.Stop();
            _scrollTimer = null;

            if (_activeScrollViewer != null)
                _activeScrollViewer.Cursor = Cursors.Arrow;

            // Remove indicator
            if (_indicator != null)
            {
                if (VisualTreeHelper.GetParent(_indicator) is Panel parent)
                    parent.Children.Remove(_indicator);
                _indicator = null;
            }

            _activeScrollViewer = null;
        }
    }
}
