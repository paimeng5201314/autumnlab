using System.Globalization;
using System.Text.RegularExpressions;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly List<Storyboard> welcomeAnimations = [];
    private readonly List<(TranslateTransform Transform, bool Reverse, double Duration)> greetingTracks = [];
    private readonly List<(StackPanel Track, string[] Words)> greetingCoverage = [];
    private readonly List<(Polyline Stroke, double Length)> signatureStrokes = [];
    private FirstRunCheckpoint visibleWelcomeCheckpoint;
    private bool welcomeAccountVisible;

    private void InitializeWelcomeExperience()
    {
        string[][] greetings = [
            ["你好", "hello", "bonjour", "こんにちは", "ciao", "안녕하세요"],
            ["hola", "مرحبا", "hallo", "สวัสดี", "olá", "नमस्ते"],
            ["hej", "γειά σου", "你好", "merhaba", "hello", "שלום"],
            ["こんにちは", "bonjour", "привет", "ciao", "안녕하세요", "hola"],
            ["namaste", "你好", "olá", "hallo", "مرحبا", "hej"],
            ["hello", "สวัสดี", "merhaba", "bonjour", "こんにちは", "γειά σου"]
        ];
        for (int row = 0; row < greetings.Length; row++)
        {
            var transform = new TranslateTransform();
            var track = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = transform, Opacity = row is 2 or 3 ? 0.13 : 0.21 };
            for (int copy = 0; copy < 2; copy++)
            {
                track.Children.Add(CreateGreetingGroup(greetings[row]));
            }
            GreetingRows.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(track, row);
            GreetingRows.Children.Add(track);
            greetingTracks.Add((transform, row % 2 == 1, 60 + row * 7));
            greetingCoverage.Add((track, greetings[row]));
        }
        GreetingViewport.SizeChanged += (_, _) =>
        {
            GreetingViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, GreetingViewport.ActualWidth, GreetingViewport.ActualHeight) };
            int copies = Math.Max(2, (int)Math.Ceiling(GreetingViewport.ActualWidth / 1920) + 1);
            foreach (var (track, words) in greetingCoverage)
                while (track.Children.Count < copies) track.Children.Add(CreateGreetingGroup(words));
        };
        // Original AutumnOS centerline artwork, shared with the locally authored brand site.
        string[] penPaths = [
            "M87 84 C79 72 60 80 52 101 C44 121 50 135 64 128 C78 120 85 99 87 84",
            "M89 83 C83 101 77 120 83 127 C91 135 105 108 111 87",
            "M111 87 C106 102 98 122 105 128 C118 138 134 108 140 88 C133 106 129 122 135 127 C144 134 157 116 165 102",
            "M165 102 C174 79 189 41 193 42 C197 43 190 65 180 87 C166 113 157 130 170 132 C181 133 196 112 209 87",
            "M156 86 C173 84 191 83 208 84",
            "M209 87 C204 103 196 123 203 130 C216 139 234 108 241 89 C234 108 229 124 236 129 C245 136 258 108 264 87",
            "M264 87 C259 101 254 119 250 129 C259 111 274 82 286 84 C298 88 286 112 282 129 C291 111 306 83 318 85 C333 87 316 116 320 126 C326 140 345 110 353 88",
            "M353 88 C347 103 342 120 339 130 C349 109 365 86 377 86 C391 88 375 117 381 127 C388 139 406 117 419 100",
            "M479 51 C455 47 433 76 430 104 C426 134 449 143 473 120 C495 99 506 59 488 51 C476 45 464 51 456 59",
            "M578 56 C584 45 574 39 561 43 C544 46 527 60 529 73 C532 87 565 91 565 106 C565 124 541 138 522 131 C511 126 514 117 522 113"
        ];
        foreach (string data in penPaths)
        {
            double[] n = Regex.Matches(data, @"-?\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            var stroke = new Polyline { Stroke = Brush("355748"), StrokeThickness = 3.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            Point start = new(n[0], n[1]); stroke.Points.Add(start); double length = 0;
            for (int j = 2; j < n.Length; j += 6)
            {
                Point previous = start;
                for (int s = 1; s <= 32; s++)
                {
                    double t = s / 32d, u = 1 - t;
                    Point p = new(u*u*u*start.X + 3*u*u*t*n[j] + 3*u*t*t*n[j+2] + t*t*t*n[j+4], u*u*u*start.Y + 3*u*u*t*n[j+1] + 3*u*t*t*n[j+3] + t*t*t*n[j+5]);
                    length += Math.Sqrt(Math.Pow(p.X-previous.X, 2) + Math.Pow(p.Y-previous.Y, 2));
                    stroke.Points.Add(p); previous = p;
                }
                start = new(n[j+4], n[j+5]);
            }
            double dash = length / stroke.StrokeThickness + 1;
            stroke.StrokeDashArray = new DoubleCollection { dash, dash };
            SignatureCanvas.Children.Add(stroke); signatureStrokes.Add((stroke, dash));
        }
        Closed += (_, _) => StopWelcomeAnimations();
    }

    private static StackPanel CreateGreetingGroup(string[] words)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Width = 1920 };
        foreach (string word in words)
            group.Children.Add(new TextBlock { Text = word, Width = 320, FontSize = 42, FontFamily = new FontFamily("Segoe UI"), Foreground = Brush("527264"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
        return group;
    }

    private void StopWelcomeAnimations()
    {
        foreach (var storyboard in welcomeAnimations) storyboard.Stop();
        welcomeAnimations.Clear();
    }

    private void AnimateWelcome(DependencyObject target, string property, double from, double to, double seconds, double delay = 0, bool repeat = false)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromSeconds(seconds), BeginTime = TimeSpan.FromSeconds(delay), EnableDependentAnimation = true };
        if (repeat) animation.RepeatBehavior = RepeatBehavior.Forever;
        else animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard(); storyboard.Children.Add(animation); welcomeAnimations.Add(storyboard); storyboard.Begin();
    }

    private void RefreshWelcomeMotion(bool enteringBrand = false)
    {
        StopWelcomeAnimations();
        bool brand = visibleWelcomeCheckpoint == FirstRunCheckpoint.Brand;
        HelloTransform.TranslateY = brand ? -92 : 0;
        HelloTransform.ScaleX = HelloTransform.ScaleY = brand ? 0.68 : 1;
        foreach (var (stroke, _) in signatureStrokes) { stroke.StrokeDashOffset = 0; stroke.Opacity = 1; }
        if (WelcomePanel.Visibility != Visibility.Visible || !systemUiSettings.AnimationsEnabled) return;
        if (visibleWelcomeCheckpoint == FirstRunCheckpoint.Hello)
            foreach (var (transform, reverse, duration) in greetingTracks)
                AnimateWelcome(transform, "X", reverse ? -1920 : 0, reverse ? 0 : -1920, duration, repeat: true);
        if (brand && enteringBrand)
        {
            AnimateWelcome(HelloTransform, "TranslateY", 0, -92, 0.7);
            AnimateWelcome(HelloTransform, "ScaleX", 1, 0.68, 0.7);
            AnimateWelcome(HelloTransform, "ScaleY", 1, 0.68, 0.7);
            double delay = 0.45;
            foreach (var (stroke, length) in signatureStrokes)
            {
                stroke.StrokeDashOffset = length;
                stroke.Opacity = 0;
                double duration = length / signatureStrokes.Sum(s => s.Length) * 2.5;
                AnimateWelcome(stroke, "Opacity", 0, 1, 0.01, delay);
                AnimateWelcome(stroke, "StrokeDashOffset", length, 0, duration, delay);
                delay += duration;
            }
        }
    }

    private void OnWelcomeAppearanceNext(object sender, RoutedEventArgs e)
    {
        welcomeAccountVisible = true;
        InitializeStage.Visibility = Visibility.Collapsed;
        WelcomeAccountStage.Visibility = Visibility.Visible;
        WelcomeStep.Text = "04 / 账号";
        RefreshFirstRunAccountUi();
        (firstRunLogin.Visibility == Visibility.Visible ? firstRunLogin : PrepareDesktopButton).Focus(FocusState.Programmatic);
    }

    private void OnWelcomeAccountBack(object sender, RoutedEventArgs e)
    {
        if (accountOperation || firstRunLoginPending || firstRunSkipPending) return;
        welcomeAccountVisible = false;
        WelcomeAccountStage.Visibility = Visibility.Collapsed;
        InitializeStage.Visibility = Visibility.Visible;
        WelcomeStep.Text = "03 / 个性化";
        WelcomeAppearanceNext.Focus(FocusState.Programmatic);
    }
}
