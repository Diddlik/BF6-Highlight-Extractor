using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace Bf6Highlights.Desktop;

/// <summary>Maps between the displayed frame and the pixels of the recording.</summary>
public static class RegionMath
{
    /// <summary>The area the uniformly scaled frame covers inside the control.</summary>
    public static (double Scale, double OffsetX, double OffsetY) Fit(
        double videoWidth, double videoHeight, double surfaceWidth, double surfaceHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
            throw new ArgumentException("Ungültige Bildgröße.");
        var scale = Math.Min(surfaceWidth / videoWidth, surfaceHeight / videoHeight);
        return (scale, (surfaceWidth - videoWidth * scale) / 2, (surfaceHeight - videoHeight * scale) / 2);
    }

    /// <summary>A dragged rectangle in control coordinates as whole pixels of the recording.</summary>
    public static RegionSettings ToVideo(double x1, double y1, double x2, double y2,
        int videoWidth, int videoHeight, double surfaceWidth, double surfaceHeight)
    {
        var (scale, offsetX, offsetY) = Fit(videoWidth, videoHeight, surfaceWidth, surfaceHeight);
        var left = ClampStart((Math.Min(x1, x2) - offsetX) / scale, videoWidth);
        var top = ClampStart((Math.Min(y1, y2) - offsetY) / scale, videoHeight);
        var right = Clamp((Math.Max(x1, x2) - offsetX) / scale, videoWidth);
        var bottom = Clamp((Math.Max(y1, y2) - offsetY) / scale, videoHeight);
        return new()
        {
            X = left, Y = top,
            Width = Math.Max(1, Math.Min(right - left, videoWidth - left)),
            Height = Math.Max(1, Math.Min(bottom - top, videoHeight - top)),
        };
    }

    public static Rect ToSurface(RegionSettings region, int videoWidth, int videoHeight,
        double surfaceWidth, double surfaceHeight)
    {
        var (scale, offsetX, offsetY) = Fit(videoWidth, videoHeight, surfaceWidth, surfaceHeight);
        return new(offsetX + region.X * scale, offsetY + region.Y * scale,
            region.Width * scale, region.Height * scale);
    }

    public static RegionSettings Move(RegionSettings region, int dx, int dy,
        int videoWidth, int videoHeight) => region with
    {
        X = Math.Clamp(region.X + dx, 0, Math.Max(0, videoWidth - region.Width)),
        Y = Math.Clamp(region.Y + dy, 0, Math.Max(0, videoHeight - region.Height)),
    };

    private static int Clamp(double value, int limit) =>
        (int)Math.Clamp(Math.Round(value, MidpointRounding.ToEven), 0, limit);

    private static int ClampStart(double value, int limit) => Clamp(value, limit - 1);
}

public sealed partial class RegionWindow : Window
{
    private int videoWidth;
    private int videoHeight;
    private Point start;
    private bool dragging;
    private bool moving;
    private RegionSettings? startRegion;
    private Bitmap? bitmap;
    private Func<double, Task<RegionFrame?>>? loadFrame;

    /// <summary>The picked region, or null when the window was closed without a choice.</summary>
    public RegionSettings? Region { get; private set; }

    public RegionWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var overlay = this.FindControl<Canvas>("Overlay")!;
        overlay.PointerPressed += Begin;
        overlay.PointerMoved += Drag;
        overlay.PointerReleased += Finish;
        SurfaceControl.SizeChanged += (_, _) => Render();
        AddHandler(KeyDownEvent, KeyPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => { Render(); overlay.Focus(); };
        Closed += (_, _) => ReplaceBitmap(null);
    }

    public RegionWindow(RegionFrame frame, RegionSettings? initialRegion,
        Func<double, Task<RegionFrame?>> frameLoader) : this()
    {
        loadFrame = frameLoader;
        Region = initialRegion;
        SetFrame(frame);
        UpdateSelection();
    }

    private Grid SurfaceControl => this.FindControl<Grid>("Surface")!;
    private Rectangle SelectionControl => this.FindControl<Rectangle>("Selection")!;

    private void Begin(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SurfaceControl).Properties.IsLeftButtonPressed) return;
        start = e.GetPosition(SurfaceControl);
        startRegion = Region;
        moving = Region is not null && SurfaceRect(Region).Contains(start);
        if (!moving) Region = null;
        dragging = true;
        e.Pointer.Capture(SurfaceControl);
        UpdateFromPointer(start);
    }

    private void Drag(object? sender, PointerEventArgs e)
    {
        if (dragging) UpdateFromPointer(e.GetPosition(SurfaceControl));
    }

    private void Finish(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
        UpdateFromPointer(e.GetPosition(SurfaceControl));
        if (!Valid(Region)) Region = null;
        UpdateSelection();
    }

    private void UpdateFromPointer(Point current)
    {
        if (moving && startRegion is not null)
        {
            var (scale, _, _) = RegionMath.Fit(videoWidth, videoHeight,
                SurfaceControl.Bounds.Width, SurfaceControl.Bounds.Height);
            Region = RegionMath.Move(startRegion,
                (int)Math.Round((current.X - start.X) / scale),
                (int)Math.Round((current.Y - start.Y) / scale), videoWidth, videoHeight);
        }
        else
            Region = RegionMath.ToVideo(start.X, start.Y, current.X, current.Y, videoWidth,
                videoHeight, SurfaceControl.Bounds.Width, SurfaceControl.Bounds.Height);
        UpdateSelection();
    }

    private Rect SurfaceRect(RegionSettings region) => RegionMath.ToSurface(region,
        videoWidth, videoHeight, SurfaceControl.Bounds.Width, SurfaceControl.Bounds.Height);

    private void Render()
    {
        if (Region is null || videoWidth <= 0 || videoHeight <= 0
            || SurfaceControl.Bounds.Width <= 0 || SurfaceControl.Bounds.Height <= 0)
        {
            SelectionControl.IsVisible = false;
            return;
        }
        var rectangle = SurfaceRect(Region);
        Canvas.SetLeft(SelectionControl, rectangle.X);
        Canvas.SetTop(SelectionControl, rectangle.Y);
        SelectionControl.Width = rectangle.Width;
        SelectionControl.Height = rectangle.Height;
        SelectionControl.IsVisible = true;
    }

    private void UpdateSelection()
    {
        Render();
        var valid = Valid(Region);
        this.FindControl<Button>("Accept")!.IsEnabled = valid;
        this.FindControl<TextBlock>("Readout")!.Text = Region is null
            ? "Kein Bereich gewählt."
            : valid
                ? $"x={Region.X} y={Region.Y} Breite={Region.Width} Höhe={Region.Height}"
                : "Auswahl zu klein, bitte mindestens 8×8 Pixel wählen.";
    }

    private static bool Valid(RegionSettings? region) => region is { Width: >= 8, Height: >= 8 };

    private void SetFrame(RegionFrame frame)
    {
        videoWidth = frame.Video.Width;
        videoHeight = frame.Video.Height;
        using var stream = new MemoryStream(frame.Png);
        ReplaceBitmap(new Bitmap(stream));
        this.FindControl<TextBox>("Timestamp")!.Text =
            frame.Timestamp.ToString("G17", CultureInfo.InvariantCulture);
        this.FindControl<TextBlock>("Hint")!.Text =
            $"{System.IO.Path.GetFileName(frame.Video.Path)} · {videoWidth}×{videoHeight} · "
            + $"Frame {Reports.FormatTimestamp(frame.Timestamp)}";
    }

    private void ReplaceBitmap(Bitmap? replacement)
    {
        this.FindControl<Image>("Frame")!.Source = replacement;
        bitmap?.Dispose();
        bitmap = replacement;
    }

    private async void LoadFrameClick(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("Timestamp")!.Text ?? "";
        if (loadFrame is null || !double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var timestamp) || !double.IsFinite(timestamp))
        {
            this.FindControl<TextBlock>("Readout")!.Text = "Bitte eine gültige Sekunde eingeben.";
            return;
        }
        var button = this.FindControl<Button>("LoadFrame")!;
        button.IsEnabled = false;
        try
        {
            if (await loadFrame(timestamp) is { } frame) SetFrame(frame);
            else this.FindControl<TextBlock>("Readout")!.Text = "Bild konnte nicht geladen werden.";
            UpdateSelection();
        }
        finally { button.IsEnabled = true; }
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; return; }
        if (e.Key == Key.Delete && e.Source is not TextBox) { Reset(); e.Handled = true; return; }
        if (e.Key == Key.Enter && e.Source is TextBox)
        {
            LoadFrameClick(null, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && Valid(Region)) { Close(); e.Handled = true; return; }
        if (Region is null || e.Source is TextBox || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        var dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        Region = RegionMath.Move(Region, dx, dy, videoWidth, videoHeight);
        UpdateSelection();
        e.Handled = true;
    }

    private void AcceptClick(object? sender, RoutedEventArgs e)
    {
        if (Valid(Region)) Close();
    }

    private void ResetClick(object? sender, RoutedEventArgs e) => Reset();

    private void Reset()
    {
        Region = null;
        UpdateSelection();
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Cancel();
    }

    private void Cancel() { Region = null; Close(); }
}
