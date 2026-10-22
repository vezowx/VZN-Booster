using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using VznBooster.Desktop.ViewModels;

namespace VznBooster.Desktop;

public partial class MainWindow : Window
{
    private const double ExpandedWidth = 248;
    private const double CollapsedWidth = 78;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnableDarkTitleBar();
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        Sidebar.Width = _vm.IsSidebarCollapsed ? CollapsedWidth : ExpandedWidth;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed)) AnimateSidebar();
        else if (e.PropertyName == nameof(MainViewModel.CurrentPage)) AnimatePage();
    }

    private void AnimateSidebar()
    {
        if (_vm is null) return;
        var target = _vm.IsSidebarCollapsed ? CollapsedWidth : ExpandedWidth;
        if (_vm.ReduceAnimations)
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = target;
            return;
        }
        Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void AnimatePage()
    {
        if (_vm is null || _vm.ReduceAnimations) return;
        var duration = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PageTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* título claro: apenas estético */ }
    }
}
