using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class BrowserCard : UserControl
{
    public static readonly DependencyProperty BrowserNameProperty = DependencyProperty.Register(nameof(BrowserName), typeof(string), typeof(BrowserCard), new PropertyMetadata("Browser"));
    public static readonly DependencyProperty DataSizeProperty = DependencyProperty.Register(nameof(DataSize), typeof(string), typeof(BrowserCard), new PropertyMetadata("0 MB"));
    public string BrowserName { get => (string)GetValue(BrowserNameProperty); set => SetValue(BrowserNameProperty, value); }
    public string DataSize { get => (string)GetValue(DataSizeProperty); set => SetValue(DataSizeProperty, value); }
    public BrowserCard() => InitializeComponent();
}
