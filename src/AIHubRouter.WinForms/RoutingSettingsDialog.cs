using System.Collections.Immutable;
using System.Drawing;
using AIHubRouter.Core;

namespace AIHubRouter.WinForms;

internal sealed record RoutingSettingsDraft(
    RoutingUiSettings Settings,
    string ActiveProbeApiKey);

internal sealed class RoutingSettingsDialog : Form
{
    private readonly NativeThemePalette _palette;
    private readonly bool _balancedMode;
    private readonly Func<RoutingSettingsDraft, Task<bool>> _applyAsync;
    private readonly Func<CancellationToken, Task> _runProbeAsync;
    private RoutingSettingsDraft _committedDraft;
    private CancellationTokenSource? _probeCancellation;
    private bool _suppressDirty;
    private bool _dirty;
    private bool _applying;
    private bool _runningProbe;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _successPercent = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 0, Dock = DockStyle.Fill };
    private readonly NumericUpDown _pollSeconds = new() { Minimum = 30, Maximum = 3_600, Increment = 30, Dock = DockStyle.Fill };
    private readonly NumericUpDown _cacheSeconds = new() { Minimum = 30, Maximum = 3_600, Increment = 30, Dock = DockStyle.Fill };
    private readonly CheckBox _autoRoute = new() { Text = "自动路由", AutoSize = true };
    private readonly ComboBox _durationCategory = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _softDeadline = new() { Minimum = 0, Maximum = 300, Increment = 0.5M, DecimalPlaces = 1, Dock = DockStyle.Fill };
    private readonly NumericUpDown _expectedOutput = new() { Minimum = 0, Maximum = 10_000_000, Increment = 100, DecimalPlaces = 0, ThousandsSeparator = true, Dock = DockStyle.Fill };
    private readonly CheckBox _probeEnabled = new() { Text = "启用固定 90 秒健康检查", AutoSize = true };
    private readonly NumericUpDown _probeKeyId = new() { Minimum = 0, Maximum = 1_000_000_000, Dock = DockStyle.Fill };
    private readonly TextBox _probeApiKey = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox _showApiKey = new() { Text = "显示", AutoSize = true };
    private readonly TextBox _probeModel = new() { Dock = DockStyle.Fill };
    private readonly Label _probeSummary = new() { AutoSize = true, Text = "未选择 Key" };
    private readonly Button _runNowButton = new() { Text = "立即检查", AutoSize = true };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _smoothRendering = new() { Text = "平滑渲染", AutoSize = true };
    private readonly TextBox _groupIds = new() { Dock = DockStyle.Fill };
    private readonly TextBox _patterns = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _okButton = new() { Text = "确定", AutoSize = true };
    private readonly Button _applyButton = new() { Text = "应用", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };

    public RoutingSettingsDialog(
        RoutingUiSettings initialSettings,
        string activeProbeApiKey,
        NativeThemePalette palette,
        bool balancedMode,
        Func<RoutingSettingsDraft, Task<bool>> applyAsync,
        Func<CancellationToken, Task> runProbeAsync)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(applyAsync);
        ArgumentNullException.ThrowIfNull(runProbeAsync);

        _palette = palette;
        _balancedMode = balancedMode;
        _applyAsync = applyAsync;
        _runProbeAsync = runProbeAsync;
        _committedDraft = new RoutingSettingsDraft(initialSettings.Normalize(), activeProbeApiKey?.Trim() ?? string.Empty);

        InitializeDialog();
        LoadDraft(_committedDraft);
        NativeThemeManager.Apply(this, _palette);
        UpdateDirtyState();
    }

    private void InitializeDialog()
    {
        Text = "路由设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(600, 450);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);

        BuildTabs();
        SetTabOrder();
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Panel(), 0, 0);
        footer.Controls.Add(_okButton, 1, 0);
        footer.Controls.Add(_applyButton, 2, 0);
        footer.Controls.Add(_cancelButton, 3, 0);
        _okButton.Click += async (_, _) => await CommitAsync(closeOnSuccess: true);
        _applyButton.Click += async (_, _) => await CommitAsync(closeOnSuccess: false);
        _cancelButton.Click += (_, _) =>
        {
            if (_runningProbe)
            {
                _probeCancellation?.Cancel();
            }
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        foreach (var control in AllEditableControls())
        {
            control.TextChanged += (_, _) => UpdateDirtyState();
            if (control is CheckBox checkBox) checkBox.CheckedChanged += (_, _) => UpdateDirtyState();
            if (control is NumericUpDown numeric) numeric.ValueChanged += (_, _) => UpdateDirtyState();
            if (control is ComboBox combo) combo.SelectedIndexChanged += (_, _) => UpdateDirtyState();
        }
        _showApiKey.CheckedChanged += (_, _) => _probeApiKey.UseSystemPasswordChar = !_showApiKey.Checked;
        _probeEnabled.CheckedChanged += (_, _) => { UpdateProbeControls(); UpdateDirtyState(); };
        _runNowButton.Click += async (_, _) => await RunProbeAsync();
        _tabs.SelectedIndexChanged += (_, _) => UpdateDirtyState();
    }

    private void BuildTabs()
    {
        _tabs.TabPages.Add(BuildRoutingPage());
        _tabs.TabPages.Add(BuildBalancedPage());
        _tabs.TabPages.Add(BuildProbePage());
        _tabs.TabPages.Add(BuildAppearancePage());
    }

    private TabPage BuildRoutingPage()
    {
        var table = CreateTable(4);
        AddRow(table, 0, "最低成功率 (%)", _successPercent);
        AddRow(table, 1, "轮询间隔（秒）", _pollSeconds);
        AddRow(table, 2, "账户缓存（秒）", _cacheSeconds);
        table.Controls.Add(_autoRoute, 1, 3);
        table.SetColumnSpan(_autoRoute, 2);
        return Page("路由与筛选", table);
    }

    private TabPage BuildBalancedPage()
    {
        _durationCategory.Items.Clear();
        _durationCategory.Items.AddRange(["短任务（< 1 小时）", "中任务（1-4 小时）", "长任务（> 4 小时）"]);
        var table = CreateTable(5);
        AddRow(table, 0, "任务规模", _durationCategory);
        AddRow(table, 1, "软截止容忍（秒）", _softDeadline);
        AddRow(table, 2, "预计输出（Token）", _expectedOutput);
        table.Controls.Add(new Label
        {
            Text = "任务规模用于速度模式的自适应估计；软截止和预计输出仅在均衡模式生效。",
            Dock = DockStyle.Fill,
            AutoSize = true
        }, 1, 3);
        table.SetColumnSpan(table.GetControlFromPosition(1, 3)!, 2);
        table.Controls.Add(new Label
        {
            Text = "均衡模式始终使用 Deadline 策略，不再按时钟切换经济模式。",
            Dock = DockStyle.Fill,
            AutoSize = true
        }, 1, 4);
        table.SetColumnSpan(table.GetControlFromPosition(1, 4)!, 2);
        if (!_balancedMode)
        {
            foreach (var control in new Control[] { _softDeadline, _expectedOutput }) control.Enabled = false;
        }
        return Page("策略参数", table);
    }

    private TabPage BuildProbePage()
    {
        var table = CreateTable(6);
        table.Controls.Add(_probeEnabled, 0, 0);
        table.SetColumnSpan(_probeEnabled, 3);
        AddRow(table, 1, "Key ID", _probeKeyId);
        var apiPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        apiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        apiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        apiPanel.Controls.Add(_probeApiKey, 0, 0);
        apiPanel.Controls.Add(_showApiKey, 1, 0);
        AddRow(table, 2, "API Key", apiPanel);
        AddRow(table, 3, "测试模型", _probeModel);
        AddRow(table, 4, "当前选择", _probeSummary);
        table.Controls.Add(_runNowButton, 1, 5);
        table.SetColumnSpan(_runNowButton, 2);
        return Page("Key 健康检查", table);
    }

    private TabPage BuildAppearancePage()
    {
        var table = CreateTable(4);
        _theme.Items.AddRange(["跟随系统", "浅色", "深色"]);
        AddRow(table, 0, "主题", _theme);
        table.Controls.Add(_smoothRendering, 1, 1);
        table.SetColumnSpan(_smoothRendering, 2);
        AddRow(table, 2, "屏蔽分组 ID", _groupIds);
        AddRow(table, 3, "屏蔽节点匹配规则", _patterns);
        table.RowStyles[3] = new RowStyle(SizeType.Percent, 100);
        return Page("外观与黑名单", table);
    }

    private static TableLayoutPanel CreateTable(int rows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = rows, AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        for (var index = 0; index < rows; index++) table.RowStyles.Add(new RowStyle(index == rows - 1 ? SizeType.Percent : SizeType.Absolute, index == rows - 1 ? 100 : 40));
        return table;
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(4) };
        page.Controls.Add(content);
        return page;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control input)
    {
        table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true }, 0, row);
        input.AccessibleName = label;
        input.AccessibleDescription = label;
        input.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(input, 1, row);
    }

    private void SetTabOrder()
    {
        var controls = new Control[]
        {
            _successPercent, _pollSeconds, _cacheSeconds, _autoRoute,
            _durationCategory, _softDeadline, _expectedOutput,
            _probeEnabled, _probeKeyId, _probeApiKey, _showApiKey, _probeModel, _runNowButton,
            _theme, _smoothRendering, _groupIds, _patterns, _okButton, _applyButton, _cancelButton
        };
        for (var index = 0; index < controls.Length; index++) controls[index].TabIndex = index;
    }

    private IEnumerable<Control> AllEditableControls()
    {
        yield return _successPercent;
        yield return _pollSeconds;
        yield return _cacheSeconds;
        yield return _autoRoute;
        yield return _durationCategory;
        yield return _softDeadline;
        yield return _expectedOutput;
        yield return _probeEnabled;
        yield return _probeKeyId;
        yield return _probeApiKey;
        yield return _probeModel;
        yield return _theme;
        yield return _smoothRendering;
        yield return _groupIds;
        yield return _patterns;
    }

    private void LoadDraft(RoutingSettingsDraft draft)
    {
        _suppressDirty = true;
        try
        {
            var settings = draft.Settings;
            _successPercent.Value = Math.Clamp(settings.MinimumSuccessPercent, 0, 100);
            _pollSeconds.Value = Math.Clamp(settings.PollingIntervalSeconds, 30, 3_600);
            _cacheSeconds.Value = Math.Clamp(settings.AccountCacheSeconds, 30, 3_600);
            _autoRoute.Checked = settings.AutoRoute;
            _durationCategory.SelectedIndex = settings.DurationCategory switch
            {
                TaskDurationCategory.Short => 0,
                TaskDurationCategory.Long => 2,
                _ => 1
            };
            _softDeadline.Value = Math.Clamp((decimal)settings.BalancedSoftDeadlineSeconds, 0, 300);
            _expectedOutput.Value = Math.Clamp((decimal)settings.BalancedExpectedOutputTokens, 0, 10_000_000);
            _probeEnabled.Checked = settings.ActiveProbeEnabled;
            _probeKeyId.Value = Math.Clamp((decimal)(settings.ActiveProbeKeyId ?? 0), 0, 1_000_000_000);
            _probeApiKey.Text = draft.ActiveProbeApiKey;
            _probeModel.Text = settings.ActiveProbeModel;
            _theme.SelectedIndex = settings.Theme switch { WinFormsTheme.Light => 1, WinFormsTheme.Dark => 2, _ => 0 };
            _smoothRendering.Checked = settings.SmoothRendering;
            _groupIds.Text = string.Join(", ", settings.BlockedGroupIds);
            _patterns.Text = string.Join(Environment.NewLine, settings.BlockedNodePatterns);
            UpdateProbeControls();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private void UpdateProbeControls()
    {
        var enabled = _probeEnabled.Checked;
        _probeKeyId.Enabled = enabled;
        _probeApiKey.Enabled = enabled;
        _showApiKey.Enabled = enabled;
        _probeModel.Enabled = enabled;
        _probeSummary.Text = enabled
            ? _probeKeyId.Value > 0 ? $"Key ID {(long)_probeKeyId.Value}" : "未选择 Key"
            : "健康检查已禁用";
    }

    private void UpdateDirtyState()
    {
        if (_suppressDirty) return;
        var settings = TryReadSettingsSnapshot(out _, showMessage: false);
        _dirty = settings is null ||
            !settings.IsEquivalentTo(_committedDraft.Settings) ||
            !string.Equals(_probeApiKey.Text.Trim(), _committedDraft.ActiveProbeApiKey, StringComparison.Ordinal);
        _applyButton.Enabled = _dirty;
        _runNowButton.Enabled = !_dirty && _committedDraft.Settings.ActiveProbeEnabled && IsProbeValid(_committedDraft, out _);
        UpdateProbeControls();
    }

    private RoutingUiSettings? TryReadSettingsSnapshot(out string error, bool showMessage)
    {
        error = string.Empty;
        var ids = new List<long>();
        foreach (var token in _groupIds.Text.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(token, out var id) || id <= 0)
            {
                error = $"屏蔽分组 ID 无效：{token}";
                if (showMessage) ShowValidation(error, _groupIds, 3);
                return null;
            }
            ids.Add(id);
        }

        return new RoutingUiSettings
        {
            MinimumSuccessPercent = (int)_successPercent.Value,
            PollingIntervalSeconds = (int)_pollSeconds.Value,
            AccountCacheSeconds = (int)_cacheSeconds.Value,
            AutoRoute = _autoRoute.Checked,
            DurationCategory = _durationCategory.SelectedIndex switch
            {
                0 => TaskDurationCategory.Short,
                2 => TaskDurationCategory.Long,
                _ => TaskDurationCategory.Medium
            },
            BalancedSoftDeadlineSeconds = (double)_softDeadline.Value,
            BalancedExpectedOutputTokens = (double)_expectedOutput.Value,
            ActiveProbeEnabled = _probeEnabled.Checked,
            ActiveProbeKeyId = _probeKeyId.Value > 0 ? (long?)_probeKeyId.Value : null,
            ActiveProbeModel = _probeModel.Text.Trim(),
            Theme = _theme.SelectedIndex switch { 1 => WinFormsTheme.Light, 2 => WinFormsTheme.Dark, _ => WinFormsTheme.System },
            SmoothRendering = _smoothRendering.Checked,
            BlockedGroupIds = ids.ToImmutableArray(),
            BlockedNodePatterns = _patterns.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray()
        }.Normalize();
    }

    private RoutingSettingsDraft? TryReadDraft(out string error, bool showMessage)
    {
        var settings = TryReadSettingsSnapshot(out error, showMessage);
        if (settings is null)
        {
            return null;
        }

        var draft = new RoutingSettingsDraft(settings, _probeApiKey.Text.Trim());
        if (!IsProbeValid(draft, out error))
        {
            if (showMessage)
            {
                Control target = draft.Settings.ActiveProbeKeyId is not > 0
                    ? _probeKeyId
                    : string.IsNullOrWhiteSpace(draft.ActiveProbeApiKey)
                        ? _probeApiKey
                        : _probeModel;
                ShowValidation(error, target, 2);
            }
            return null;
        }
        return draft;
    }

    private static bool IsProbeValid(RoutingSettingsDraft draft, out string error)
    {
        if (!draft.Settings.ActiveProbeEnabled)
        {
            error = string.Empty;
            return true;
        }
        if (draft.Settings.ActiveProbeKeyId is not > 0)
        {
            error = "启用健康检查时必须填写 Key ID。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(draft.ActiveProbeApiKey))
        {
            error = "启用健康检查时必须填写 API Key。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(draft.Settings.ActiveProbeModel))
        {
            error = "启用健康检查时必须填写测试模型。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private void ShowValidation(string message, Control control, int tabIndex)
    {
        _tabs.SelectedIndex = tabIndex;
        control.Focus();
        MessageBox.Show(this, message, "设置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task CommitAsync(bool closeOnSuccess)
    {
        if (_applying || _runningProbe) return;
        var draft = TryReadDraft(out _, showMessage: true);
        if (draft is null) return;
        if (!_dirty && !closeOnSuccess) return;
        _applying = true;
        var enabledStates = CaptureAndDisableCommitControls();
        try
        {
            if (!await _applyAsync(draft))
            {
                MessageBox.Show(this, "设置应用失败。", "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _committedDraft = draft;
            _dirty = false;
            _runNowButton.Enabled = _committedDraft.Settings.ActiveProbeEnabled && IsProbeValid(_committedDraft, out _);
            if (closeOnSuccess) DialogResult = DialogResult.OK;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _applying = false;
            if (!IsDisposed && !Disposing)
            {
                foreach (var pair in enabledStates) pair.Key.Enabled = pair.Value;
                UpdateDirtyState();
                _applyButton.Enabled = _dirty;
            }
        }
    }

    private IEnumerable<Control> CommitControls()
    {
        yield return _tabs;
        foreach (var control in AllEditableControls()) yield return control;
        yield return _okButton;
        yield return _applyButton;
        yield return _runNowButton;
        yield return _showApiKey;
        yield return _cancelButton;
    }

    private Dictionary<Control, bool> CaptureAndDisableCommitControls()
    {
        var controls = CommitControls().Distinct().ToArray();
        var enabledStates = controls.ToDictionary(control => control, control => control.Enabled);
        foreach (var control in controls) control.Enabled = false;
        return enabledStates;
    }

    private async Task RunProbeAsync()
    {
        if (_applying || _runningProbe || _dirty || !_committedDraft.Settings.ActiveProbeEnabled || !IsProbeValid(_committedDraft, out _)) return;
        _runningProbe = true;
        var enabledStates = CaptureAndDisableCommitControls();
        var cancelText = _cancelButton.Text;
        var cancelDialogResult = _cancelButton.DialogResult;
        using var cancellation = new CancellationTokenSource();
        _probeCancellation = cancellation;
        _cancelButton.Text = "取消检查";
        _cancelButton.DialogResult = DialogResult.None;
        _cancelButton.Enabled = true;
        try { await _runProbeAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed && !Disposing)
            {
                MessageBox.Show(this, exception.Message, "健康检查", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_probeCancellation, cancellation))
            {
                _probeCancellation = null;
            }
            _runningProbe = false;
            if (!IsDisposed && !Disposing)
            {
                foreach (var pair in enabledStates) pair.Key.Enabled = pair.Value;
                _cancelButton.Text = cancelText;
                _cancelButton.DialogResult = cancelDialogResult;
                UpdateDirtyState();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _probeCancellation?.Cancel();
            _suppressDirty = true;
            try { _probeApiKey.Clear(); }
            finally { _suppressDirty = false; }
        }
        base.Dispose(disposing);
    }
}
