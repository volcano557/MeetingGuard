using MeetingAgent.App.Data;
using MeetingAgent.App.ViewModels;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MeetingAgent.App.Auth;

/// <summary>
/// 登录窗口：启动时显示，登录成功后通过 LoggedInUser 暴露给宿主，
/// 然后宿主关闭本窗、显示主程序窗口。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public DbUser? LoggedInUser { get; private set; }

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.LoginSucceeded += OnLoginSucceeded;
    }

    private void OnLoginSucceeded(DbUser user)
    {
        LoggedInUser = user;
        DialogResult = true;   // 主程序使用 ShowDialog 判定
        Close();
    }

    // ─── 自定义标题栏（拖动 + 最小化 + 关闭） ───────────────────
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ─── PasswordBox 绑定（Password 不支持直接绑定） ────────────
    private void LoginPwdBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb) _vm.LoginPassword = pb.Password;
    }
    private void RegPwdBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb) _vm.RegPassword = pb.Password;
    }
    private void RegPwd2Box_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb) _vm.RegPassword2 = pb.Password;
    }
}

// ─── 简单转换器 ────────────────────────────────────────────────

public sealed class BoolToVisConverter : IValueConverter
{
    public static readonly BoolToVisConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class VisInverseConverter : IValueConverter
{
    public static readonly VisInverseConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NotEmptyToVisConverter : IValueConverter
{
    public static readonly NotEmptyToVisConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
