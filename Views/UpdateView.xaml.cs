using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 更新窗口的正文（版本对照 + 发行说明 + 下载进度 + 几个按钮）。
///
/// <see cref="ViewModel" /> 是依赖属性：父级（<see cref="UpdateWindow" />）在
/// <c>InitializeComponent</c> 之后才赋值，普通 CLR 属性此时已经错过了 x:Bind 的求值时机。
/// </summary>
public sealed partial class UpdateView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(UpdateViewModel),
        typeof(UpdateView),
        new PropertyMetadata(null));

    public UpdateView() => InitializeComponent();

    /// <summary>更新模型（整个应用共用一份，见 <see cref="MainViewModel.Update" />）。</summary>
    public UpdateViewModel? ViewModel
    {
        get => (UpdateViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>承载本控件的窗口（「稍后」要关掉它）。</summary>
    public Window? HostWindow { get; set; }

    private void Close_Click(object sender, RoutedEventArgs e) => HostWindow?.Close();
}
