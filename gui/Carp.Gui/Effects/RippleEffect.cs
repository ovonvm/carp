using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Carp.Gui.Effects;

/// <summary>
/// 点击涟漪：在按钮模板的 RippleLayer Canvas 上放一个半透明圆，
/// 从点击位置向外扩散并淡出（同心圆扩散，单色实现，开销很小）。
/// </summary>
public static class RippleEffect
{
    private static readonly Duration Lifetime = new(TimeSpan.FromMilliseconds(430));

    public static void Play(Button button, Point origin, byte tint = 64)
    {
        if (button?.Template == null || !button.IsLoaded)
        {
            return;
        }
        if (button.Template.FindName("RippleLayer", button) is not Canvas layer)
        {
            return;
        }

        var width = layer.ActualWidth;
        var height = layer.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = Math.Max(width, height) * 0.85;
        var size = radius * 2;

        var brush = new SolidColorBrush(Color.FromArgb(tint, 0xFF, 0xFF, 0xFF));
        brush.Freeze();

        var scale = new ScaleTransform(0.02, 0.02);
        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            Opacity = 0.7,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };

        Canvas.SetLeft(ellipse, origin.X - (size / 2));
        Canvas.SetTop(ellipse, origin.Y - (size / 2));
        layer.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        layer.Children.Add(ellipse);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var growX = new DoubleAnimation(0.02, 1, Lifetime) { EasingFunction = ease };
        var growY = new DoubleAnimation(0.02, 1, Lifetime) { EasingFunction = ease };
        var fade = new DoubleAnimation(0.6, 0, Lifetime);

        growX.Completed += (_, _) =>
        {
            layer.Children.Remove(ellipse);
            if (layer.Children.Count == 0)
            {
                layer.Clip = null;
            }
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, growX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, growY);
        ellipse.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
