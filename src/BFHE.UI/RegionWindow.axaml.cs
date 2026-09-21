using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Globalization;
using System.Threading.Channels;
using Mat = OpenCvSharp.Mat;
using OpenCVException = OpenCvSharp.OpenCVException;
using VideoCapture = OpenCvSharp.VideoCapture;
using VideoCaptureProperties = OpenCvSharp.VideoCaptureProperties;

namespace Bf6Highlights.Ui;

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
    private readonly Channel<(long Id, double Timestamp)> frameRequests =
        Channel.CreateBounded<(long, double)>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly CancellationTokenSource previewCancellation = new();
    private Bitmap? bitmap;
    private long frameRequest;
    private bool updatingSeek;
    private bool closed;

    /// <summary>The picked region, or null when the window was closed without a choice.</summary>
    public RegionSettings? Region { get; private set; }

    public RegionWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var overlay = this.FindControl<Canvas>("Overlay")!;
        overlay.PointerPressed += Begin;
        overlay.PointerMoved += Drag;
        overlay.PointerReleased += Finish;
        overlay.PointerCaptureLost += CaptureLost;
        var seek = this.FindControl<Slider>("Seek")!;
        seek.PropertyChanged += SeekChanged;
        SurfaceControl.SizeChanged += (_, _) => Render();
        AddHandler(KeyDownEvent, KeyPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => { Render(); overlay.Focus(); };
        Closed += (_, _) => DisposePreview();
    }

    public RegionWindow(RegionFrame frame, RegionSettings? initialRegion) : this()
    {
        Region = initialRegion;
        SetFrame(frame);
        UpdateSelection();
        StartPreview(frame.Video.Path, frame.Timestamp);
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
        e.Pointer.Capture((Canvas)sender!);
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
        UpdateFromPointer(e.GetPosition(SurfaceControl));
        e.Pointer.Capture(null);
        if (!Valid(Region)) Region = null;
        moving = false;
        startRegion = null;
        UpdateSelection();
    }

    private void CaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        dragging = false;
        moving = false;
        startRegion = null;
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
        this.FindControl<TextBox>("Timestamp")!.Text =
            frame.Timestamp.ToString("G17", CultureInfo.InvariantCulture);
        var seek = this.FindControl<Slider>("Seek")!;
        updatingSeek = true;
        seek.Maximum = Math.Max(0, frame.Video.DurationSeconds - 0.001);
        seek.Value = Math.Clamp(frame.Timestamp, seek.Minimum, seek.Maximum);
        updatingSeek = false;
        this.FindControl<TextBlock>("CurrentTime")!.Text = Reports.FormatTimestamp(frame.Timestamp);
        this.FindControl<TextBlock>("DurationTime")!.Text = Reports.FormatTimestamp(frame.Video.DurationSeconds);
        this.FindControl<TextBlock>("Hint")!.Text =
            $"{System.IO.Path.GetFileName(frame.Video.Path)} · {videoWidth}×{videoHeight} · "
            + $"Frame {Reports.FormatTimestamp(frame.Timestamp)}";
    }

    private void StartPreview(string sourcePath, double timestamp)
    {
        _ = Task.Run(() => PreviewLoop(sourcePath, previewCancellation.Token));
        QueueFrame(timestamp);
    }

    private async Task PreviewLoop(string sourcePath, CancellationToken token)
    {
        try
        {
            using var capture = new VideoCapture(sourcePath);
            if (!capture.IsOpened()) throw new IOException("Video konnte nicht geöffnet werden.");
            using var image = new Mat();
            await foreach (var request in frameRequests.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                capture.Set(VideoCaptureProperties.PosMsec, request.Timestamp * 1000d);
                if (!capture.Read(image) || image.Empty())
                    throw new IOException("Frame konnte nicht gelesen werden.");
                var png = image.ToBytes(".png");
                if (request.Id != Interlocked.Read(ref frameRequest)) continue;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!closed && request.Id == Interlocked.Read(ref frameRequest))
                        ReplaceBitmap(png);
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or OpenCVException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!closed) this.FindControl<TextBlock>("Readout")!.Text = error.Message;
            });
        }
    }

    private void LoadFrameClick(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("Timestamp")!.Text ?? "";
        if (!double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var timestamp) || !double.IsFinite(timestamp))
        {
            this.FindControl<TextBlock>("Readout")!.Text = "Bitte eine gültige Sekunde eingeben.";
            return;
        }
        SeekTo(timestamp);
    }

    private void SeekChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (updatingSeek || e.Property != RangeBase.ValueProperty) return;
        var timestamp = this.FindControl<Slider>("Seek")!.Value;
        UpdateTime(timestamp);
        QueueFrame(timestamp);
    }

    private void SeekTo(double timestamp)
    {
        var seek = this.FindControl<Slider>("Seek")!;
        timestamp = Math.Clamp(timestamp, seek.Minimum, seek.Maximum);
        updatingSeek = true;
        seek.Value = timestamp;
        updatingSeek = false;
        UpdateTime(timestamp);
        QueueFrame(timestamp);
    }

    private void UpdateTime(double timestamp)
    {
        this.FindControl<TextBox>("Timestamp")!.Text =
            timestamp.ToString("G17", CultureInfo.InvariantCulture);
        this.FindControl<TextBlock>("CurrentTime")!.Text = Reports.FormatTimestamp(timestamp);
    }

    private void QueueFrame(double timestamp)
    {
        if (closed) return;
        var id = Interlocked.Increment(ref frameRequest);
        frameRequests.Writer.TryWrite((id, timestamp));
    }

    private void ReplaceBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var replacement = new Bitmap(stream);
        this.FindControl<Image>("Frame")!.Source = replacement;
        bitmap?.Dispose();
        bitmap = replacement;
    }

    private void DisposePreview()
    {
        if (closed) return;
        closed = true;
        previewCancellation.Cancel();
        frameRequests.Writer.TryComplete();
        this.FindControl<Image>("Frame")!.Source = null;
        bitmap?.Dispose();
        bitmap = null;
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
