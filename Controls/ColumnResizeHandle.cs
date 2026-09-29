using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;

namespace Exdir.Controls;

/// <summary>
/// 列头右边缘的拖动把手（宽度可拖）。
/// 与 <see cref="PaneSplitter"/> 的区别：这里需要“拖动开始/拖动结束”两个时机
/// （拖动开始时要以当前渲染宽度为基准，因为名称列可能是自动填满的），
/// 另外双击用于把该列恢复默认宽度。
/// 自己只管报告，不改列宽——列宽由宿主视图写进 <c>ColumnLayout</c>。
/// </summary>
public sealed class ColumnResizeHandle : Grid
{
    /// <summary>命中区域宽度（像素），实际画出来的是一条 1px 竖线。</summary>
    public const double HandleWidth = 6;

    private readonly Border _line;

    private bool _isDragging;
    private bool _isPointerOver;
    private double _lastX;

    public ColumnResizeHandle()
    {
        Width = HandleWidth;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        UseLayoutRounding = true;

        _line = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        Children.Add(_line);

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>开始拖动（此时宿主应记下当前渲染宽度）。</summary>
    public event EventHandler? DragStarted;

    /// <summary>相对上一次事件的水平位移（像素），正值表示向右。</summary>
    public event EventHandler<double>? DeltaChanged;

    /// <summary>拖动结束（宿主可在此落盘）。</summary>
    public event EventHandler? DragCompleted;

    /// <summary>双击：恢复默认列宽。</summary>
    public event EventHandler? ResetRequested;

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        SetHighlight(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        if (!_isDragging)
        {
            SetHighlight(false);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _lastX = e.GetCurrentPoint(null).Position.X;
        CapturePointer(e.Pointer);
        SetHighlight(true);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = e.GetCurrentPoint(null).Position.X;
        var delta = current - _lastX;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        _lastX = current;
        DeltaChanged?.Invoke(this, delta);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleasePointerCapture(e.Pointer);

        // 拖动期间 PointerExited 不会触发（指针被捕获），所以这里按当前位置重新算悬停状态
        var position = e.GetCurrentPoint(this).Position;
        _isPointerOver = position.X >= 0 && position.X <= ActualWidth && position.Y >= 0 && position.Y <= ActualHeight;

        SetHighlight(_isPointerOver);
        DragCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _isPointerOver = false;
        SetHighlight(false);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResetRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void SetHighlight(bool highlight)
    {
        _line.Background = highlight
            ? Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush) && brush is Brush accent
                ? accent
                : new SolidColorBrush(Microsoft.UI.Colors.Gray)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
