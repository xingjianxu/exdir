using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;

namespace Exdir.Controls;

/// <summary>
/// 极简分隔条。WinUI 3 没有内建 GridSplitter，这里用 Grid 派生实现：
/// 本身是透明的命中区域，内部画一条 1px 分隔线；拖动时抛出 <see cref="DeltaChanged"/> 事件，
/// 由宿主窗口调整对应的 ColumnDefinition.Width。
/// </summary>
public sealed class PaneSplitter : Grid
{
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(PaneSplitter),
        new PropertyMetadata(null, OnLineBrushChanged));

    private readonly Border _line;

    private bool _isDragging;
    private bool _isPointerOver;
    private double _lastPosition;

    public PaneSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        UseLayoutRounding = true;

        _line = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };

        Children.Add(_line);

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    /// <summary>拖动过程中水平位移（像素），正值表示向右。</summary>
    public event EventHandler<double>? DeltaChanged;

    /// <summary>分隔线画刷。请在 XAML 中传入 <c>{ThemeResource DividerStrokeColorDefaultBrush}</c>。</summary>
    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    private static void OnLineBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PaneSplitter splitter)
        {
            splitter._line.Background = (Brush?)e.NewValue;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        SetHover(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        if (!_isDragging)
        {
            SetHover(false);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _lastPosition = e.GetCurrentPoint(null).Position.X;
        CapturePointer(e.Pointer);
        SetHover(true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = e.GetCurrentPoint(null).Position.X;
        var delta = current - _lastPosition;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        _lastPosition = current;
        DeltaChanged?.Invoke(this, delta);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
        SetHover(_isPointerOver);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _isDragging = false;

    private void EndDrag(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        _isDragging = false;
        ReleasePointerCapture(pointer);
    }

    private void SetHover(bool hover)
    {
        Background = hover
            ? (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush) && brush is Brush accent
                ? accent
                : new SolidColorBrush(Microsoft.UI.Colors.Gray))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        Opacity = 1;
    }
}
