using System.Diagnostics;
using System.Drawing;

namespace AIHubRouter.WinForms;

internal sealed class AuthGuideDialog : Form
{
    private const string TokenCommand = "copy(localStorage.getItem('auth_token')); localStorage.getItem('auth_token')";
    private const string UserAgentCommand = "copy(navigator.userAgent); navigator.userAgent";
    private readonly string _baseUrl;
    private readonly Label _statusLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(20, 110, 65) };

    public AuthGuideDialog(string baseUrl)
    {
        _baseUrl = baseUrl;
        InitializeUi();
    }

    private void InitializeUi()
    {
        Text = "AIHub 认证向导";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 500);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 7
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        root.Controls.Add(new Label
        {
            Text = "连接 AIHub 账号",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(25, 35, 45),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        root.Controls.Add(CreateTextStep(
            "1  推荐：邮箱和密码自动登录",
            "主窗口直接填写 AIHub 邮箱和密码。程序启动时复用 session；临近过期先刷新，refresh 失效后再重新登录。"), 0, 1);
        root.Controls.Add(CreateCommandStep(
            "2  高级备用：获取登录 Token",
            "不希望保存账号密码时，可网页登录后在 Console 执行；若显示 null，说明当前页面没有这个 localStorage 项：",
            TokenCommand,
            "复制 Token 命令"), 0, 2);
        root.Controls.Add(CreateCommandStep(
            "3  高级备用：获取浏览器 User-Agent",
            "若服务端启用了会话 UA 绑定再填写。请先粘贴 Token，再执行本条，避免覆盖剪贴板：",
            UserAgentCommand,
            "复制 UA 命令"), 0, 3);
        root.Controls.Add(CreateTextStep(
            "4  高级备用：Cookie（可选）",
            "通常只填 Token 和 User-Agent 即可。需要 Cookie 时，可从浏览器开发者工具的 Network 请求头复制完整 Cookie。"), 0, 4);
        root.Controls.Add(new Label
        {
            Text = "勾选“常态化保存认证”后，邮箱、密码、access/refresh token、Cookie 和 UA 都由 Windows DPAPI 加密，仅当前 Windows 用户可解密。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(80, 90, 100),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 5);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var openButton = new Button { Text = "打开 AIHub 登录页", AutoSize = true };
        openButton.Click += (_, _) => OpenLoginPage();
        var closeButton = new Button { Text = "完成", AutoSize = true, DialogResult = DialogResult.OK };
        actions.Controls.Add(openButton, 0, 0);
        actions.Controls.Add(_statusLabel, 1, 0);
        actions.Controls.Add(closeButton, 2, 0);
        root.Controls.Add(actions, 0, 6);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.Add(root);
    }

    private Control CreateCommandStep(
        string title,
        string description,
        string command,
        string buttonText)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0, 3, 0, 3)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.Controls.Add(CreateTitle(title), 0, 0);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, 0)!, 2);
        panel.Controls.Add(new Label
        {
            Text = description,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(70, 80, 90),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, 1)!, 2);
        panel.Controls.Add(new TextBox
        {
            Text = command,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(246, 248, 250)
        }, 0, 2);
        var copyButton = new Button { Text = buttonText, AutoSize = true, Dock = DockStyle.Fill };
        copyButton.Click += (_, _) => CopyCommand(command);
        panel.Controls.Add(copyButton, 1, 2);
        return panel;
    }

    private static Control CreateTextStep(string title, string description)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 3, 0, 3)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(CreateTitle(title), 0, 0);
        panel.Controls.Add(new Label
        {
            Text = description,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(70, 80, 90),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        return panel;
    }

    private static Label CreateTitle(string title)
    {
        return new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 45, 55),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void CopyCommand(string command)
    {
        try
        {
            Clipboard.SetText(command);
            _statusLabel.Text = "命令已复制";
        }
        catch (Exception exception)
        {
            _statusLabel.ForeColor = Color.FromArgb(185, 45, 45);
            _statusLabel.Text = $"复制失败：{exception.Message}";
        }
    }

    private void OpenLoginPage()
    {
        try
        {
            if (!Uri.TryCreate(_baseUrl.Trim(), UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("站点地址无效。");
            }

            var loginUri = new Uri(new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/"), "login");
            Process.Start(new ProcessStartInfo(loginUri.ToString()) { UseShellExecute = true });
            _statusLabel.Text = "已打开登录页";
        }
        catch (Exception exception)
        {
            _statusLabel.ForeColor = Color.FromArgb(185, 45, 45);
            _statusLabel.Text = $"打开失败：{exception.Message}";
        }
    }
}
