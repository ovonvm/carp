using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Carp.Gui.Effects;

/// <summary>
/// 「启动 / 停止」卡片上的雷达波纹：两圈细环从卡片中心向外扩散并淡出，
/// 只在服务运行中播放；停止后立刻清掉，不占用渲染线程。
/// 环画在按钮模板自带的 RippleLayer Canvas 上，并裁剪到卡片圆角内。
/// </summary>
public static class RadarEffect
{
    private const string LayerName = "RippleLayer";

    /// <summary>与设计稿一致：rgba(47,154,78,.45) 的细环。</summary>
    private static readonly Color RingColor = Color.FromArgb(115, 47, 154, 78);

    /// <summary>一圈从中心扩散到边缘的时长；第二圈延迟半个周期接力。</summary>
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2.4);

    public static void Run(Button? button, bool on)
    {
        if (button?.Template == null)
        {
            return;
        }
        if (!button.IsLoaded)
        {
            // 首次 ApplyState 可能早于模板加载，加载完再补一次。
            button.Loaded += (_, _) => Run(button, on);
            return;
        }
        if (button.Template.FindName(LayerName, button) is not Canvas layer)
        {
            return;
        }

        layer.Children.Clear();
        if (!on)
        {
            layer.Clip = null;
            return;
        }

        var width = layer.ActualWidth;
        var height = layer.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        layer.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 10, 10);

        var brush = new SolidColorBrush(RingColor);
        brush.Freeze();

        // 环的最终直径取卡片对角线稍大一点，扩散到边缘时刚好被裁掉。
        var diameter = Math.Sqrt((width * width) + (height * height)) * 1.02;
        var centerX = width / 2;
        var centerY = height / 2;

        for (var i = 0; i < 2; i++)
        {
            var scale = new ScaleTransform(0.06, 0.06);
            var ring = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Stroke = brush,
                StrokeThickness = 1,
                Fill = null,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            Canvas.SetLeft(ring, centerX - (diameter / 2));
            Canvas.SetTop(ring, centerY - (diameter / 2));
            layer.Children.Add(ring);

            var begin = TimeSpan.FromMilliseconds(Period.TotalMilliseconds * i / 2);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var growX = new DoubleAnimation(0.06, 1, Period)
            {
                BeginTime = begin,
                EasingFunction = ease,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            var growY = new DoubleAnimation(0.06, 1, Period)
            {
                BeginTime = begin,
                EasingFunction = ease,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            var fade = new DoubleAnimation(0.55, 0, Period)
            {
                BeginTime = begin,
                EasingFunction = ease,
                RepeatBehavior = RepeatBehavior.Forever,
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, growX);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, growY);
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
