using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace CmdWorkspace.Core;

/// <summary>Short fades and slides used everywhere (about 0.13 to 0.2 seconds, so the app feels smooth and never slow).</summary>
public static class Anim
{
    private static readonly ConditionalWeakTable<SolidColorBrush, Storyboard> Running = new();
    private static readonly Windows.UI.ViewManagement.UISettings Sys = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Storyboard> Active = new();

    /// <summary>Stops the last fade or slide on an element and keeps the opacity it shows now. Without this an old animation can hold the opacity (for example 0) and hide the element after animations were switched off.</summary>
    private static void Cancel(FrameworkElement element)
    {
        if (!Active.TryGetValue(element, out var old)) return;
        var now = element.Opacity;
        Active.Remove(element);
        old.Stop();
        element.Opacity = now;
    }

    private static void Begin(FrameworkElement element, Storyboard story)
    {
        Active.Remove(element);
        Active.Add(element, story);
        story.Begin();
    }

    /// <summary>False when the user switched animations off in Settings or Windows has Animation effects off. Then everything jumps to its end value.</summary>
    public static bool Enabled
    {
        get
        {
            if (!AppState.S.Animations) return false;
            try { return Sys.AnimationsEnabled; }
            catch { return true; }
        }
    }

    /// <summary>Fades a brush to another colour.</summary>
    public static void ColorTo(SolidColorBrush brush, Color to, int ms = 130)
    {
        if (!Enabled) { brush.Color = to; return; }
        if (Running.TryGetValue(brush, out var old))
        {
            var now = brush.Color;
            old.Stop();
            brush.Color = now;
            Running.Remove(brush);
        }
        var animation = new ColorAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, brush);
        Storyboard.SetTargetProperty(animation, "Color");
        var story = new Storyboard();
        story.Children.Add(animation);
        story.Completed += (_, _) =>
        {
            brush.Color = to;
            story.Stop();
            if (Running.TryGetValue(brush, out var current) && ReferenceEquals(current, story)) Running.Remove(brush);
        };
        Running.Add(brush, story);
        story.Begin();
    }

    /// <summary>Appears with a fade and a small slide (dx, dy are the starting offset in DIPs).</summary>
    public static void PopIn(FrameworkElement element, double dx = 0, double dy = -6, int ms = 150, int delayMs = 0)
    {
        Cancel(element);
        if (!Enabled) { element.Opacity = 1; return; }
        var move = new TranslateTransform { X = dx, Y = dy };
        element.RenderTransform = move;
        element.Opacity = 0;
        var story = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        var fade = new DoubleAnimation { To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(ms)) };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (dx != 0)
        {
            var slide = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = ease };
            Storyboard.SetTarget(slide, move);
            Storyboard.SetTargetProperty(slide, "X");
            story.Children.Add(slide);
        }
        if (dy != 0)
        {
            var slide = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = ease };
            Storyboard.SetTarget(slide, move);
            Storyboard.SetTargetProperty(slide, "Y");
            story.Children.Add(slide);
        }
        Begin(element, story);
    }

    /// <summary>Fades an element to an opacity.</summary>
    public static void Fade(FrameworkElement element, double to, int ms = 180)
    {
        Cancel(element);
        if (!Enabled) { element.Opacity = to; return; }
        var fade = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)) };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard();
        story.Children.Add(fade);
        Begin(element, story);
    }

    /// <summary>Slides a transform's X to a value (toggle knob).</summary>
    public static void SlideX(TranslateTransform transform, double to, int ms = 150)
    {
        if (!Enabled) { transform.X = to; return; }
        var slide = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        var story = new Storyboard();
        story.Children.Add(slide);
        story.Begin();
    }

    private static readonly ConditionalWeakTable<TranslateTransform, Storyboard> SlidingY = new();
    private static readonly ConditionalWeakTable<GradientStop, Storyboard> RunningStops = new();

    /// <summary>Sets Y at once and stops a running slide of the same transform.</summary>
    public static void SetY(TranslateTransform transform, double y)
    {
        if (SlidingY.TryGetValue(transform, out var old)) { SlidingY.Remove(transform); old.Stop(); }
        transform.Y = y;
    }

    /// <summary>Slides a transform's Y to a value (the selection highlight in the left bar). Starts from where it is now.</summary>
    public static void SlideY(TranslateTransform transform, double to, int ms = 180)
    {
        if (!Enabled) { SetY(transform, to); return; }
        var now = transform.Y;
        SetY(transform, now);
        var slide = new DoubleAnimation { From = now, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        var story = new Storyboard();
        story.Children.Add(slide);
        story.Completed += (_, _) => { transform.Y = to; story.Stop(); if (SlidingY.TryGetValue(transform, out var cur) && ReferenceEquals(cur, story)) SlidingY.Remove(transform); };
        SlidingY.Add(transform, story);
        story.Begin();
    }

    /// <summary>Fades a gradient colour stop to another colour (the window background when the theme changes).</summary>
    public static void ColorTo(GradientStop stop, Color to, int ms = 130)
    {
        if (!Enabled) { stop.Color = to; return; }
        if (RunningStops.TryGetValue(stop, out var old)) { var now = stop.Color; RunningStops.Remove(stop); old.Stop(); stop.Color = now; }
        var animation = new ColorAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, stop);
        Storyboard.SetTargetProperty(animation, "Color");
        var story = new Storyboard();
        story.Children.Add(animation);
        story.Completed += (_, _) =>
        {
            stop.Color = to;
            story.Stop();
            if (RunningStops.TryGetValue(stop, out var cur) && ReferenceEquals(cur, story)) RunningStops.Remove(stop);
        };
        RunningStops.Add(stop, story);
        story.Begin();
    }

    /// <summary>Fades an element out, slides it left and shrinks its height to 0. The task ends when it is finished. The element is NOT reset (the caller rebuilds it).</summary>
    public static Task CollapseAsync(FrameworkElement element, double dx, int ms = 180)
    {
        var tcs = new TaskCompletionSource();
        Cancel(element);
        if (!Enabled || element.ActualHeight <= 0) { tcs.SetResult(); return tcs.Task; }
        var height = element.ActualHeight;
        element.Height = height;
        element.IsHitTestVisible = false;
        var move = new TranslateTransform();
        element.RenderTransform = move;
        var story = new Storyboard();
        var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(ms * 0.7)) };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        var slide = new DoubleAnimation { To = dx, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(slide, move);
        Storyboard.SetTargetProperty(slide, "X");
        story.Children.Add(slide);
        var shrink = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(shrink, element);
        Storyboard.SetTargetProperty(shrink, "Height");
        story.Children.Add(shrink);
        story.Completed += (_, _) => tcs.TrySetResult();
        Begin(element, story);
        return tcs.Task;
    }

    /// <summary>Disappears with a fade and a small slide, then calls done (to close a popup). Without animations done runs at once.</summary>
    public static void PopOut(FrameworkElement element, double dx, double dy, int ms, Action? done)
    {
        Cancel(element);
        if (!Enabled) { done?.Invoke(); return; }
        var move = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = move;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var story = new Storyboard();
        var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(ms)) };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        foreach (var (prop, value) in new[] { ("X", dx), ("Y", dy) })
        {
            if (value == 0) continue;
            var slide = new DoubleAnimation { To = value, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = ease };
            Storyboard.SetTarget(slide, move);
            Storyboard.SetTargetProperty(slide, prop);
            story.Children.Add(slide);
        }
        // done runs first (closes or hides the element), then the story is stopped so opacity and offset go back to their normal values.
        story.Completed += (_, _) => { done?.Invoke(); story.Stop(); };
        Begin(element, story);
    }
}
