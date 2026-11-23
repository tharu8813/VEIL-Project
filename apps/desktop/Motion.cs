using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Veil.Desktop;

/// <summary>
/// Small choreography helpers. Entrance animations run from an offset back to the element's
/// real value and then release (FillBehavior.Stop), so layout and hit testing never depend on
/// a held animation. Everything collapses to an instant change when Windows animations are off.
/// </summary>
internal static class Motion
{
    internal static bool Enabled =>
        SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) > 0;

    internal static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    internal static readonly IEasingFunction Expo = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };
    internal static readonly IEasingFunction Sine = new SineEase { EasingMode = EasingMode.EaseInOut };
    internal static readonly IEasingFunction Spring = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut };

    static Motion()
    {
        foreach (var ease in new[] { Out, Expo, Sine, Spring })
            ((Freezable)ease).Freeze();
    }

    /// <summary>Holds <paramref name="from"/> until the delay passes, then eases to <paramref name="to"/>.</summary>
    internal static DoubleAnimationUsingKeyFrames Keys(double from, double to, double delayMs, double ms, IEasingFunction? ease = null)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMs > 0)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms)), ease ?? Expo));
        return animation;
    }

    internal static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null, double delayMs = 0) =>
        new(to, TimeSpan.FromMilliseconds(Enabled ? ms : 0))
        {
            EasingFunction = ease ?? Out,
            BeginTime = TimeSpan.FromMilliseconds(Enabled ? delayMs : 0),
        };

    internal static (ScaleTransform Scale, TranslateTransform Move) Transforms(UIElement element, Point? origin = null)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] })
            return (s, t);
        var scale = new ScaleTransform();
        var move = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { scale, move } };
        element.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        return (scale, move);
    }

    /// <summary>Fade in while travelling from an offset and scale. Optional blur resolves into focus.</summary>
    internal static void Enter(UIElement element, double delayMs, double dx = 0, double dy = 0,
        double scale = 1, double ms = 700, double blur = 0, Point? origin = null, IEasingFunction? ease = null)
    {
        if (!Enabled) return;
        var (s, t) = Transforms(element, origin);
        ease ??= Expo;
        element.BeginAnimation(UIElement.OpacityProperty, Keys(0, 1, delayMs, ms * 0.6, Out));
        if (dx != 0) t.BeginAnimation(TranslateTransform.XProperty, Keys(dx, 0, delayMs, ms, ease));
        if (dy != 0) t.BeginAnimation(TranslateTransform.YProperty, Keys(dy, 0, delayMs, ms, ease));
        if (scale != 1)
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(scale, 1, delayMs, ms, ease));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(scale, 1, delayMs, ms, ease));
        }
        if (blur > 0)
        {
            var effect = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Performance };
            element.Effect = effect;
            var focus = Keys(blur, 0, delayMs, ms * 0.8, Out);
            // Drop the effect afterwards so text renders crisply again.
            focus.Completed += (_, _) => { if (element.Effect == effect) element.Effect = null; };
            effect.BeginAnimation(BlurEffect.RadiusProperty, focus);
        }
    }

    /// <summary>Left-to-right reveal through a feathered mask; the content itself never distorts.</summary>
    internal static void Wipe(UIElement element, double delayMs, double ms = 650)
    {
        if (!Enabled) return;
        var lead = new GradientStop(Colors.White, 0);
        var tail = new GradientStop(Colors.Transparent, 0);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), GradientStops = { lead, tail } };
        element.OpacityMask = mask;
        var reveal = Keys(-0.15, 1, delayMs, ms, Expo);
        reveal.Completed += (_, _) => { if (element.OpacityMask == mask) element.OpacityMask = null; };
        lead.BeginAnimation(GradientStop.OffsetProperty, reveal);
        tail.BeginAnimation(GradientStop.OffsetProperty, Keys(0, 1.15, delayMs, ms, Expo));
    }

    /// <summary>Quick overshoot "pop" used to acknowledge an action.</summary>
    internal static void Pop(ScaleTransform scale, double peak = 1.45)
    {
        if (!Enabled) return;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), Out));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), Spring));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    internal static void Spin(RotateTransform rotate, double ms = 650)
    {
        if (!Enabled) return;
        rotate.BeginAnimation(RotateTransform.AngleProperty, Keys(0, 360, 0, ms, Expo));
    }

    /// <summary>Endless loop; pass null easing for linear motion.</summary>
    internal static void Loop(IAnimatable target, DependencyProperty property, double from, double to,
        double seconds, bool reverse = false, IEasingFunction? ease = null, double delayMs = 0)
    {
        if (!Enabled) return;
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = reverse,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        });
    }

    internal static void Stop(IAnimatable target, DependencyProperty property) => target.BeginAnimation(property, null);
}
