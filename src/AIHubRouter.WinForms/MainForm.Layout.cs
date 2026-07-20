using System.Drawing;

namespace AIHubRouter.WinForms;

internal sealed partial class MainForm
{
    private readonly TextBox _baseUrlText = new() { Text = "https://aihub.top", PlaceholderText = "AIHub 站点地址" };
    private readonly TextBox _emailText = new() { PlaceholderText = "AIHub 登录邮箱" };
    private readonly TextBox _passwordText = new() { UseSystemPasswordChar = true, PlaceholderText = "AIHub 登录密码" };
    private readonly TextBox _tokenText = new() { UseSystemPasswordChar = true, PlaceholderText = "备用：auth_token 或 Bearer Token" };
    private readonly TextBox _cookieText = new() { UseSystemPasswordChar = true, PlaceholderText = "可选：完整 Cookie 请求头" };
    private readonly TextBox _userAgentText = new() { UseSystemPasswordChar = true, PlaceholderText = "推荐：登录浏览器的 User-Agent" };
    private readonly CheckBox _showCredentialsCheck = new() { Text = "显示凭据", AutoSize = true };
    private readonly CheckBox _advancedAuthenticationCheck = new() { Text = "展开高级认证（Token / Cookie / UA）", AutoSize = true };
    private readonly TableLayoutPanel _advancedAuthenticationPanel = new();
    private readonly RowStyle _credentialPanelRowStyle = new(SizeType.Absolute, 270);
    private readonly Button _authGuideButton = new() { Text = "认证向导", AutoSize = true, Width = 132 };
    private readonly Button _openLoginButton = new() { Text = "打开登录页", AutoSize = true, Width = 132 };
    private readonly Button _pasteTokenButton = new() { Text = "粘贴", AutoSize = true };
    private readonly Button _pasteCookieButton = new() { Text = "粘贴", AutoSize = true };
    private readonly Button _pasteUserAgentButton = new() { Text = "粘贴", AutoSize = true };
    private readonly Button _resetBaseUrlButton = new() { Text = "默认", AutoSize = true };
    private readonly Button _validateButton = new() { Text = "验证认证", AutoSize = true };
    private readonly CheckBox _persistCredentialsCheck = new() { Text = "常态化保存认证", AutoSize = true };
    private readonly Button _saveSettingsButton = new() { Text = "保存当前配置", AutoSize = true };
    private readonly Button _refreshButton = new() { Text = "刷新数据", AutoSize = true };
    private readonly Button _routeNowButton = new() { Text = "立即路由", AutoSize = true };
    private readonly ComboBox _platformCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly NumericUpDown _minimumSuccessInput = new()
    {
        Minimum = 0,
        Maximum = 100,
        DecimalPlaces = 0,
        Value = 0,
        Width = 72
    };
    private readonly NumericUpDown _intervalInput = new()
    {
        Minimum = 30,
        Maximum = 3600,
        Increment = 30,
        Value = 60,
        Width = 80
    };
    private readonly CheckBox _autoRouteCheck = new() { Text = "自动路由", AutoSize = true };
    private readonly CheckBox _verticalSyncCheck = new() { Text = "垂直同步（双缓冲）", AutoSize = true, Checked = true };
    private readonly BufferedDataGridView _providerGrid = CreateGrid();
    private readonly BufferedDataGridView _keyGrid = CreateGrid();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 100 };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _candidateLabel = new() { Text = "最低价：-" };
    private readonly ToolStripProgressBar _progressBar = new()
    {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 25,
        Visible = false,
        Width = 90
    };

    private void InitializeUi()
    {
        SuspendLayout();
        Text = "AIHub 最低价路由器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 880);
        ClientSize = new Size(1220, 950);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(245, 247, 249);
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _platformCombo.Items.AddRange(["openai", "anthropic", "gemini", "antigravity", "grok"]);
        _platformCombo.SelectedIndex = 0;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(_credentialPanelRowStyle);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildCredentialPanel(), 0, 0);
        root.Controls.Add(BuildRoutingToolbar(), 0, 1);
        root.Controls.Add(BuildDataArea(), 0, 2);

        _statusStrip.Items.AddRange([_statusLabel, _candidateLabel, _progressBar]);
        _statusStrip.SizingGrip = false;
        _statusStrip.BackColor = Color.White;

        Controls.Add(root);
        Controls.Add(_statusStrip);
        ConfigureProviderGrid();
        ConfigureKeyGrid();
        ConfigureGuidance();
        ResumeLayout(true);
    }

    private Control BuildCredentialPanel()
    {
        var group = new GroupBox
        {
            Text = "连接与认证",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
            BackColor = Color.White
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 7
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var quickGuide = new Label
        {
            Text = "输入邮箱和密码即可自动登录；session 到期时先刷新，刷新失效后再自动登录。",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 75, 105),
            TextAlign = ContentAlignment.MiddleLeft
        };
        table.Controls.Add(quickGuide, 0, 0);
        table.SetColumnSpan(quickGuide, 3);

        AddCredentialRow(table, 1, "站点地址", _baseUrlText, _resetBaseUrlButton);
        AddCredentialRow(table, 2, "邮箱地址", _emailText, new Label());
        AddCredentialRow(table, 3, "登录密码", _passwordText, new Label());

        _advancedAuthenticationCheck.Dock = DockStyle.Fill;
        _advancedAuthenticationCheck.Padding = new Padding(0, 3, 0, 0);
        table.Controls.Add(_advancedAuthenticationCheck, 0, 4);
        table.SetColumnSpan(_advancedAuthenticationCheck, 3);

        BuildAdvancedAuthenticationPanel();
        table.Controls.Add(_advancedAuthenticationPanel, 0, 5);
        table.SetColumnSpan(_advancedAuthenticationPanel, 3);

        var persistencePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        persistencePanel.Controls.Add(_persistCredentialsCheck);
        persistencePanel.Controls.Add(_saveSettingsButton);
        persistencePanel.Controls.Add(new Label
        {
            Text = "邮箱、密码、session、Cookie 和 UA 均使用 Windows DPAPI 加密",
            AutoSize = true,
            Margin = new Padding(10, 6, 0, 0),
            ForeColor = Color.FromArgb(75, 85, 95)
        });
        table.Controls.Add(persistencePanel, 0, 6);
        table.SetColumnSpan(persistencePanel, 3);

        var authActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10, 2, 0, 0)
        };
        authActions.Controls.Add(_authGuideButton);
        authActions.Controls.Add(_openLoginButton);
        authActions.Controls.Add(_validateButton);
        authActions.Controls.Add(_showCredentialsCheck);
        table.Controls.Add(authActions, 3, 0);
        table.SetRowSpan(authActions, 7);
        group.Controls.Add(table);
        return group;
    }

    private void BuildAdvancedAuthenticationPanel()
    {
        _advancedAuthenticationPanel.Dock = DockStyle.Fill;
        _advancedAuthenticationPanel.ColumnCount = 3;
        _advancedAuthenticationPanel.RowCount = 3;
        _advancedAuthenticationPanel.Visible = false;
        _advancedAuthenticationPanel.Margin = Padding.Empty;
        _advancedAuthenticationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _advancedAuthenticationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _advancedAuthenticationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        for (var row = 0; row < 3; row++)
        {
            _advancedAuthenticationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        }

        AddCredentialRow(_advancedAuthenticationPanel, 0, "登录 Token", _tokenText, _pasteTokenButton);
        AddCredentialRow(_advancedAuthenticationPanel, 1, "Cookie（可选）", _cookieText, _pasteCookieButton);
        AddCredentialRow(_advancedAuthenticationPanel, 2, "User-Agent", _userAgentText, _pasteUserAgentButton);
    }

    private void ToggleAdvancedAuthentication()
    {
        var expanded = _advancedAuthenticationCheck.Checked;
        _advancedAuthenticationCheck.Text = expanded
            ? "收起高级认证（Token / Cookie / UA）"
            : "展开高级认证（Token / Cookie / UA）";
        _advancedAuthenticationPanel.Visible = expanded;
        _credentialPanelRowStyle.Height = expanded ? 380 : 270;
        if (_advancedAuthenticationPanel.Parent is TableLayoutPanel table)
        {
            table.RowStyles[5].Height = expanded ? 108 : 0;
        }
    }

    private Control BuildRoutingToolbar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 12, 4, 8),
            BackColor = BackColor
        };

        panel.Controls.Add(CreateToolbarLabel("平台"));
        panel.Controls.Add(_platformCombo);
        panel.Controls.Add(CreateToolbarLabel("最低 6h 可用率"));
        panel.Controls.Add(_minimumSuccessInput);
        panel.Controls.Add(CreateToolbarLabel("%"));
        panel.Controls.Add(CreateToolbarLabel("轮询间隔"));
        panel.Controls.Add(_intervalInput);
        panel.Controls.Add(CreateToolbarLabel("秒"));
        panel.Controls.Add(_autoRouteCheck);
        panel.Controls.Add(_verticalSyncCheck);
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(_routeNowButton);
        return panel;
    }

    private Control BuildDataArea()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 7,
            BackColor = BackColor
        };

        var splitInitialized = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitInitialized || split.Height < 380)
            {
                return;
            }

            const int panel1Minimum = 180;
            const int panel2Minimum = 160;
            var availableHeight = split.Height - split.SplitterWidth;
            var preferredDistance = availableHeight * 3 / 5;
            split.SplitterDistance = Math.Clamp(
                preferredDistance,
                panel1Minimum,
                availableHeight - panel2Minimum);
            split.Panel1MinSize = panel1Minimum;
            split.Panel2MinSize = panel2Minimum;
            splitInitialized = true;
        };

        var providers = new GroupBox
        {
            Text = "供应商监测（来自 /providers）",
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        providers.Controls.Add(_providerGrid);
        split.Panel1.Controls.Add(providers);

        var keys = new GroupBox
        {
            Text = "API Keys（勾选需要自动路由的 Key）",
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        keys.Controls.Add(_keyGrid);
        split.Panel2.Controls.Add(keys);
        return split;
    }

    private void ConfigureProviderGrid()
    {
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "推荐", DataPropertyName = "Best", Width = 58 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "分组", DataPropertyName = "GroupId", Width = 62 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "方案", DataPropertyName = "Plan", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 150 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "平台", DataPropertyName = "Platform", Width = 85 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "公开倍率", DataPropertyName = "PublicRate", Width = 82 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "实际倍率", DataPropertyName = "EffectiveRate", Width = 82 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = "State", Width = 105 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "6h 可用率", DataPropertyName = "Success6h", Width = 92 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "首 Token", DataPropertyName = "FirstToken", Width = 88 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "检测时间", DataPropertyName = "CheckedAt", Width = 118 });
        _providerGrid.CellFormatting += (_, eventArgs) =>
        {
            if (_providerGrid.Rows[eventArgs.RowIndex].DataBoundItem is ProviderGridRow { IsBest: true })
            {
                _providerGrid.Rows[eventArgs.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(226, 244, 234);
                _providerGrid.Rows[eventArgs.RowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(20, 83, 45);
            }
        };
    }

    private void ConfigureKeyGrid()
    {
        _keyGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "路由", DataPropertyName = "Selected", Width = 58 });
        _keyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = "Id", Width = 68, ReadOnly = true });
        _keyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180, ReadOnly = true });
        _keyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = "Status", Width = 100, ReadOnly = true });
        _keyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "分组 ID", DataPropertyName = "GroupId", Width = 80, ReadOnly = true });
        _keyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前分组", DataPropertyName = "GroupName", Width = 220, ReadOnly = true });
        _keyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "平台", DataPropertyName = "Platform", Width = 100, ReadOnly = true });
        _keyGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_keyGrid.IsCurrentCellDirty)
            {
                _keyGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
    }

    private static BufferedDataGridView CreateGrid()
    {
        return new BufferedDataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            GridColor = Color.FromArgb(225, 229, 234),
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 32 },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 243, 246),
                ForeColor = Color.FromArgb(45, 55, 65),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(249, 250, 251)
            }
        };
    }

    private static void AddCredentialRow(
        TableLayoutPanel table,
        int row,
        string label,
        Control input,
        Control helper)
    {
        var text = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 75)
        };
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(3, 4, 3, 4);
        helper.Dock = DockStyle.Fill;
        helper.Margin = new Padding(5, 4, 3, 4);
        table.Controls.Add(text, 0, row);
        table.Controls.Add(input, 1, row);
        table.Controls.Add(helper, 2, row);
    }

    private void ConfigureGuidance()
    {
        _toolTip.SetToolTip(_baseUrlText, "默认使用 https://aihub.top，也可填写兼容 Sub2API 的其他站点。");
        _toolTip.SetToolTip(_emailText, "用于自动登录 AIHub；与密码一起加密保存后可无人值守续期。");
        _toolTip.SetToolTip(_passwordText, "refresh session 被服务端拒绝后，程序才会使用邮箱和密码重新登录。");
        _toolTip.SetToolTip(_tokenText, "高级备用入口。邮箱和密码为空时，可直接填写 Bearer Token。");
        _toolTip.SetToolTip(_cookieText, "可选。站点当前主要使用 Bearer Token，单独 Cookie 通常不能访问 Keys。");
        _toolTip.SetToolTip(_userAgentText, "建议填写登录浏览器的 navigator.userAgent，以兼容服务端会话绑定。");
        _toolTip.SetToolTip(_verticalSyncCheck, "Windows 桌面由 DWM 合成；此开关控制窗口和表格双缓冲，减少刷新与滚动闪烁。");
        _toolTip.SetToolTip(_authGuideButton, "打开完整认证步骤和可复制的浏览器命令。");
        _toolTip.SetToolTip(_persistCredentialsCheck, "勾选后，账号、session、Cookie 和 UA 会通过 Windows DPAPI 加密保存到当前用户目录。");
        _toolTip.SetToolTip(_saveSettingsButton, "立即保存连接、认证、Key 勾选和路由界面配置。");
        _toolTip.SetToolTip(_providerGrid, "拖动列分隔线时调整右侧列：向左扩宽右侧列，向右缩窄右侧列。");
        _toolTip.SetToolTip(_keyGrid, "拖动列分隔线时调整右侧列：向左扩宽右侧列，向右缩窄右侧列。");
    }

    private static Label CreateToolbarLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(10, 7, 3, 0),
            ForeColor = Color.FromArgb(55, 65, 75)
        };
    }
}
