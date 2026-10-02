using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Exdir.Helpers;

/// <summary>
/// 详细信息视图的布局度量：列宽（同步状态 / 名称 / 修改日期 / 类型 / 大小）与行高。
///
/// 这里要区分两个概念：
/// <list type="bullet">
/// <item><b>requested</b>：用户拖出来的宽度，持久化到 config.json；</item>
/// <item><b>rendered</b>：真正画到界面上的宽度，由 <see cref="FitTo"/> 按窗格可用宽度算出来。</item>
/// </list>
/// 列头与每一行都绑定 rendered（同一个对象），所以三者永远对齐。
///
/// 两种布局模式：
/// <list type="bullet">
/// <item><b>自动模式</b>（<see cref="AutoFit"/>，默认）：列宽随窗格自适应——富余宽度给名称列，
///       窗格不够宽时各列按余量等比压缩，任何窗格尺寸下都能看到全部列；</item>
/// <item><b>手动模式</b>：用户拖过任意列边界后进入。列宽就是用户拖的值，窗格装不下时横向滚动
///       （同资源管理器），这样“拖到哪就是哪”，不会被自动压缩弹回去。</item>
/// </list>
/// 双击列边界可回到自动模式/默认宽度。
///
/// 首列的“同步状态”只在云同步目录里才存在（<see cref="ShowSyncColumn"/>）：隐藏时把它算成 0 宽，
/// 列定义本身不动，也就不会影响其它列的下标与持久化的列宽。
///
/// 为什么不用静态列宽：<c>ColumnDefinition.Width</c> 是 <see cref="GridLength"/>，
/// XAML 不会为资源值做类型转换，所以列宽只能用强类型属性 + <c>x:Bind</c> 共享
/// （见 AGENTS.md“踩过的坑”第 1 条）。
/// </summary>
public sealed class ColumnLayout : ObservableObject
{
    /// <summary>列数（同步状态 / 名称 / 修改日期 / 类型 / 大小）。</summary>
    public const int ColumnCount = 5;

    /// <summary>同步状态列的下标。放在最前面，因为它只是个图标（像资源管理器叠在图标旁）。</summary>
    public const int SyncStateIndex = 0;

    /// <summary>名称列的下标（拿富余宽度的就是它）。</summary>
    public const int NameIndex = 1;

    /// <summary>名称列最小宽度（像素）。</summary>
    public const double NameMinWidth = 80;

    /// <summary>其余列最小宽度（像素）。</summary>
    public const double NumberMinWidth = 44;

    public const double DefaultSyncStateWidth = 56;
    public const double DefaultNameWidth = 320;
    public const double DefaultDateWidth = 136;
    public const double DefaultTypeWidth = 104;
    public const double DefaultSizeWidth = 86;

    /// <summary>状态列最小宽度：刚好放得下列头“状态”两个字再加排序字形。</summary>
    public const double SyncStateMinWidth = 40;

    /// <summary>
    /// 文件列表行高的默认值（DIP）。比 24 的“密集”行高更舒展一些，但仍属于紧凑密度：
    /// 16 DIP 的图标与名称文字在行内垂直居中后上下各留 6 DIP。
    /// </summary>
    public const double DefaultRowHeight = 28;

    /// <summary>行高下限：行内 18 DIP 的展开箭头与 16 DIP 的图标都还放得下。</summary>
    public const double MinRowHeight = 20;

    /// <summary>行高上限：再高就不像紧凑的文件管理器了，一屏能看到的行数也会太少。</summary>
    public const double MaxRowHeight = 48;

    /// <summary>设置里行高的步进（DIP），滑块的 StepFrequency 用它。</summary>
    public const double RowHeightStep = 2;

    /// <summary>行/列头的左右内边距之和（6 + 6），与 Themes/ExdirTheme.xaml 的 ExRowPadding 保持一致。</summary>
    public const double RowPaddingWidth = 12;

    private static readonly double[] Defaults =
        { DefaultSyncStateWidth, DefaultNameWidth, DefaultDateWidth, DefaultTypeWidth, DefaultSizeWidth };

    private static readonly double[] Minimums =
        { SyncStateMinWidth, NameMinWidth, NumberMinWidth, NumberMinWidth, NumberMinWidth };

    private readonly double[] _requested = (double[])Defaults.Clone();
    private readonly double[] _rendered = (double[])Defaults.Clone();

    private bool _autoFillName = true;
    private bool _autoFit = true;
    private bool _showSyncColumn;
    private double _rowHeight = DefaultRowHeight;

    public ColumnLayout() => _rendered[SyncStateIndex] = 0;

    /// <summary>用户拖动（或恢复默认）列宽后触发，供 ViewModel 写入设置。</summary>
    public event EventHandler? RequestedChanged;

    /// <summary>实际渲染宽度变化后触发，供视图重新摆放拖动把手。</summary>
    public event EventHandler? RenderedChanged;

    public GridLength SyncStateWidth => LengthOf(SyncStateIndex);

    public GridLength NameWidth => LengthOf(NameIndex);

    public GridLength DateWidth => LengthOf(2);

    public GridLength TypeWidth => LengthOf(3);

    public GridLength SizeWidth => LengthOf(4);

    /// <summary>列宽总和（数据行的最小宽度还要加上左右内边距）。</summary>
    public double TotalWidth => _rendered[0] + _rendered[1] + _rendered[2] + _rendered[3] + _rendered[4];

    /// <summary>数据行的最小宽度：列宽总和 + 行左右内边距。</summary>
    public double RowMinWidth => TotalWidth + RowPaddingWidth;

    /// <summary>
    /// 是否显示“同步状态”列（由 <c>FolderTabViewModel</c> 按当前目录是否有云同步状态决定）。
    /// 隐藏时该列宽度算作 0，列头与单元格也会一并隐藏。
    /// </summary>
    public bool ShowSyncColumn
    {
        get => _showSyncColumn;
        set
        {
            if (!SetProperty(ref _showSyncColumn, value))
            {
                return;
            }

            _rendered[SyncStateIndex] = value ? _requested[SyncStateIndex] : 0;
            NotifyRendered();
        }
    }

    /// <summary>
    /// 文件列表每一行的行高（DIP），由设置窗口里的「行高」决定（默认 <see cref="DefaultRowHeight" />）。
    ///
    /// 为什么放在这里、而不是每个行对象上：行模板本来就绑定着本对象（列宽用的是同一个实例），
    /// 所以“改一次设置 → 同一标签页的所有行立刻跟着变”不需要任何额外管道，
    /// 也不用给几千个行对象各备一份值。行高只改变上下留白，
    /// 图标与名称依旧是“行内垂直居中”（见 Views/DetailsView.xaml）。
    /// </summary>
    public double RowHeight
    {
        get => _rowHeight;
        set
        {
            var clamped = NormalizeRowHeight(value);
            if (Math.Abs(clamped - _rowHeight) < 0.01)
            {
                return;
            }

            _rowHeight = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 把任意来源的行高夹进可用范围并取整（config.json 可能被手改过，
    /// 或旧版本写下的值超出了现在的区间）。
    /// </summary>
    public static double NormalizeRowHeight(double value)
        => double.IsFinite(value)
            ? Math.Clamp(Math.Round(value), MinRowHeight, MaxRowHeight)
            : DefaultRowHeight;

    /// <summary>整体自适应窗格宽度（默认开启；拖动过列宽后关闭，双击列边界可恢复）。</summary>
    public bool AutoFit
    {
        get => _autoFit;
        set
        {
            if (SetProperty(ref _autoFit, value))
            {
                RequestedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>名称列是否吃掉窗格里的富余宽度（与资源管理器一致：名称列负责填满剩余空间）。</summary>
    public bool AutoFillName
    {
        get => _autoFillName;
        set
        {
            if (SetProperty(ref _autoFillName, value))
            {
                RequestedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>列头显示名（用于拖动把手的无障碍名称）。</summary>
    public static string GetColumnName(int index) => index switch
    {
        0 => "状态",
        1 => "名称",
        2 => "修改日期",
        3 => "类型",
        4 => "大小",
        _ => "列",
    };

    /// <summary>当前实际画出来的宽度。</summary>
    public double GetRenderedWidth(int index) => _rendered[index];

    /// <summary>用户设定的宽度。</summary>
    public double GetRequestedWidth(int index) => _requested[index];

    /// <summary>设置用户宽度（拖动过程中调用）；低于该列最小值会被夹住。</summary>
    public void SetRequestedWidth(int index, double width)
    {
        if ((uint)index >= ColumnCount)
        {
            return;
        }

        var clamped = Math.Round(Math.Max(Minimums[index], width));
        if (Math.Abs(clamped - _requested[index]) < 0.5)
        {
            return;
        }

        _requested[index] = clamped;

        // 手动拖过名称列边界后就不再自动填满，整体也不再自适应（与资源管理器一致）
        if (index == NameIndex)
        {
            AutoFillName = false;
        }

        AutoFit = false;

        RequestedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>双击列边界：该列恢复默认宽度，并回到整体自适应模式。</summary>
    public void ResetColumn(int index)
    {
        if ((uint)index >= ColumnCount)
        {
            return;
        }

        _requested[index] = Defaults[index];

        if (index == NameIndex)
        {
            AutoFillName = true;
        }

        AutoFit = true;

        RequestedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>从设置里恢复列宽与布局模式。</summary>
    public void Apply(IReadOnlyList<double>? widths, bool autoFillName, bool autoFit)
    {
        if (widths is not null && widths.Count >= ColumnCount)
        {
            for (var i = 0; i < ColumnCount; i++)
            {
                var value = widths[i];
                _requested[i] = double.IsFinite(value) && value > 0
                    ? Math.Max(Minimums[i], Math.Round(value))
                    : Defaults[i];
            }
        }

        _autoFillName = autoFillName;
        _autoFit = autoFit;

        OnPropertyChanged(nameof(AutoFillName));
        OnPropertyChanged(nameof(AutoFit));

        Array.Copy(_requested, _rendered, ColumnCount);
        _rendered[SyncStateIndex] = _showSyncColumn ? _requested[SyncStateIndex] : 0;
        NotifyRendered();
    }

    /// <summary>当前列宽快照（持久化用）。</summary>
    public double[] ToArray() => (double[])_requested.Clone();

    /// <summary>按窗格可用宽度（DIP，已扣掉左右内边距与滚动条）计算实际渲染宽度。</summary>
    public void FitTo(double availableWidth)
    {
        if (availableWidth <= 0)
        {
            return;
        }

        var rendered = (double[])_requested.Clone();

        // 隐藏的同步状态列不占宽度（其余计算都建立在它之后）
        if (!_showSyncColumn)
        {
            rendered[SyncStateIndex] = 0;
        }

        if (_autoFillName || _autoFit)
        {
            var others = rendered[2] + rendered[3] + rendered[4] + rendered[SyncStateIndex];
            rendered[NameIndex] = Math.Max(rendered[NameIndex], availableWidth - others);
        }

        var total = rendered[0] + rendered[1] + rendered[2] + rendered[3] + rendered[4];
        if (_autoFit && total > availableWidth)
        {
            ShrinkColumns(rendered, total - availableWidth);
        }

        var changed = false;
        for (var i = 0; i < ColumnCount; i++)
        {
            if (Math.Abs(_rendered[i] - rendered[i]) > 0.01)
            {
                _rendered[i] = rendered[i];
                changed = true;
            }
        }

        if (changed)
        {
            NotifyRendered();
        }
    }

    /// <summary>各列按“距离最小值的余量”等比压缩；某一列到底后重新分配余量，故跑几轮。</summary>
    private static void ShrinkColumns(double[] widths, double amount)
    {
        for (var pass = 0; pass < ColumnCount && amount > 0.5; pass++)
        {
            var slack = 0d;
            for (var i = 0; i < ColumnCount; i++)
            {
                slack += Math.Max(0, widths[i] - Minimums[i]);
            }

            if (slack <= 0.01)
            {
                return;
            }

            var take = Math.Min(amount, slack);
            for (var i = 0; i < ColumnCount; i++)
            {
                var room = Math.Max(0, widths[i] - Minimums[i]);
                if (room > 0)
                {
                    widths[i] -= take * (room / slack);
                }
            }

            amount -= take;
        }
    }

    private GridLength LengthOf(int index) => new(_rendered[index], GridUnitType.Pixel);

    private void NotifyRendered()
    {
        OnPropertyChanged(nameof(SyncStateWidth));
        OnPropertyChanged(nameof(NameWidth));
        OnPropertyChanged(nameof(DateWidth));
        OnPropertyChanged(nameof(TypeWidth));
        OnPropertyChanged(nameof(SizeWidth));
        OnPropertyChanged(nameof(TotalWidth));
        OnPropertyChanged(nameof(RowMinWidth));
        RenderedChanged?.Invoke(this, EventArgs.Empty);
    }
}
