using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAgent.App.Auth;
using MeetingAgent.App.Data;
using System.Windows.Media;

namespace MeetingAgent.App.ViewModels;

/// <summary>
/// 登录窗 ViewModel：负责登录 / 注册 / 验证码三类交互。
/// 登录成功后通过 LoginSucceeded 事件通知宿主窗口启动主程序。
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly CaptchaService  _captcha = new();

    public LoginViewModel(DatabaseService db)
    {
        _db = db;
        RefreshCaptcha();
    }

    /// <summary>登录成功事件，宿主订阅以打开主窗并关闭登录窗。</summary>
    public event Action<DbUser>? LoginSucceeded;

    // ─── 子页切换 (true = 注册, false = 登录) ───────────────────
    [ObservableProperty] private bool isRegisterMode = false;

    [RelayCommand] private void ShowLogin()    { IsRegisterMode = false; ClearMessages(); RefreshCaptcha(); }
    [RelayCommand] private void ShowRegister() { IsRegisterMode = true;  ClearMessages(); RefreshCaptcha(); }

    // ─── 登录字段 ──────────────────────────────────────────────
    [ObservableProperty] private string loginUsername = "admin";
    [ObservableProperty] private string loginPassword = string.Empty;
    [ObservableProperty] private string loginCaptchaInput = string.Empty;
    [ObservableProperty] private bool   rememberMe = true;

    // ─── 注册字段 ──────────────────────────────────────────────
    [ObservableProperty] private string regUsername = string.Empty;
    [ObservableProperty] private string regPassword = string.Empty;
    [ObservableProperty] private string regPassword2 = string.Empty;
    [ObservableProperty] private string regEmail   = string.Empty;
    [ObservableProperty] private string regRole    = "参会人员";
    [ObservableProperty] private string regCaptchaInput = string.Empty;

    public IReadOnlyList<string> RoleOptions { get; } = new[]
    {
        "参会人员", "安全审计员", "面试官", "访客"
    };

    // ─── 验证码 ────────────────────────────────────────────────
    [ObservableProperty] private ImageSource? captchaImage;

    [RelayCommand]
    private void RefreshCaptcha()
    {
        var (_, img) = _captcha.Generate();
        CaptchaImage = img;
    }

    // ─── 提示信息 ─────────────────────────────────────────────
    [ObservableProperty] private string errorMessage   = string.Empty;
    [ObservableProperty] private string successMessage = string.Empty;
    [ObservableProperty] private bool   isBusy         = false;

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;
    }

    // ─── 登录 ─────────────────────────────────────────────────
    [RelayCommand]
    private async Task DoLoginAsync()
    {
        ClearMessages();

        if (string.IsNullOrWhiteSpace(LoginUsername)) { ErrorMessage = "⚠ 请输入用户名"; return; }
        if (string.IsNullOrEmpty(LoginPassword))      { ErrorMessage = "⚠ 请输入密码"; return; }
        if (!_captcha.Verify(LoginCaptchaInput))
        {
            ErrorMessage = "⚠ 验证码不正确";
            LoginCaptchaInput = string.Empty;
            RefreshCaptcha();
            return;
        }

        IsBusy = true;
        try
        {
            // PBKDF2 验证略耗时（~50–100 ms），放到后台线程
            var result = await Task.Run(() => _db.Login(LoginUsername.Trim(), LoginPassword));
            if (!result.Success)
            {
                ErrorMessage = $"⚠ {result.Error}";
                LoginCaptchaInput = string.Empty;
                RefreshCaptcha();
                return;
            }

            SuccessMessage = $"✓ 欢迎回来，{result.User!.Username}";
            LoginSucceeded?.Invoke(result.User);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── 注册 ─────────────────────────────────────────────────
    [RelayCommand]
    private async Task DoRegisterAsync()
    {
        ClearMessages();

        if (string.IsNullOrWhiteSpace(RegUsername)) { ErrorMessage = "⚠ 请输入用户名"; return; }
        if (string.IsNullOrEmpty(RegPassword) || RegPassword.Length < 6)
        {
            ErrorMessage = "⚠ 密码至少 6 位";
            return;
        }
        if (RegPassword != RegPassword2)
        {
            ErrorMessage = "⚠ 两次输入的密码不一致";
            return;
        }
        if (!_captcha.Verify(RegCaptchaInput))
        {
            ErrorMessage = "⚠ 验证码不正确";
            RegCaptchaInput = string.Empty;
            RefreshCaptcha();
            return;
        }

        IsBusy = true;
        try
        {
            var (ok, err) = await Task.Run(() =>
                _db.Register(RegUsername.Trim(), RegPassword, RegRole, RegEmail.Trim()));
            if (!ok) { ErrorMessage = $"⚠ {err}"; RefreshCaptcha(); RegCaptchaInput = string.Empty; return; }

            SuccessMessage = $"✓ 注册成功，用户名：{RegUsername.Trim()}，请使用新账户登录";
            // 切回登录页并预填用户名
            LoginUsername = RegUsername.Trim();
            RegUsername = RegPassword = RegPassword2 = RegEmail = RegCaptchaInput = string.Empty;
            IsRegisterMode = false;
            RefreshCaptcha();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        ErrorMessage = "💡 找回密码需联系管理员（admin@meetingguard.local）";
    }
}
