using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dropshelf.Items;
using Dropshelf.Services;

namespace Dropshelf.Activities.Downloads;

/// <summary>
/// A file's icon (or picture thumbnail) on the island that can be dragged out as the file itself (a copy, never a move) or
/// clicked to open it. It is a button, so pressing it never opens the island.
/// </summary>
internal sealed class DragOutTile : Button
{
    private const double Size = 30;

    private readonly string _path;
    private Point _press;
    private bool _pressed;
    private bool _dragged;

    public DragOutTile(string path)
    {
        _path = path;
        Width = Size;
        Height = Size;
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = "Drag the file out, or click to open it";
        VerticalAlignment = VerticalAlignment.Center;
        Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

        var image = new Image { Width = Size, Height = Size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Content = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = image };
        _ = LoadIconAsync(image);
    }

    private async System.Threading.Tasks.Task LoadIconAsync(Image image)
    {
        try
        {
            var (icon, thumbnail) = await IconLoader.LoadAsync(_path, isFolder: false);
            image.Source = icon;
            if (thumbnail)
            {
                image.Stretch = Stretch.UniformToFill;
                image.Clip = new RectangleGeometry(new Rect(0, 0, Size, Size), 7, 7);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Icon load failed for {_path}", ex);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _press = e.GetPosition(this);
        _pressed = true;
        _dragged = false;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = false;
            return;
        }

        Point p = e.GetPosition(this);
        if (Math.Abs(p.X - _press.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _press.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressed = false;
        _dragged = true;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { _path });
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Error("Dragging the download out failed", ex);
        }
    }

    protected override void OnClick()
    {
        if (_dragged)
        {
            _dragged = false;
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not open {_path}", ex);
        }
    }
}
