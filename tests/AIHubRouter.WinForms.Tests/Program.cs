using System.Reflection;
using AIHubRouter.Core;
using AIHubRouter.WinForms;

namespace AIHubRouter.WinForms.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = 0;
        foreach (var (name, body) in new (string Name, Action Body)[]
        {
            ("Apply preserves intrinsic enabled states", TestApplyPreservesIntrinsicEnabledStates),
            ("Probe preserves intrinsic enabled states", TestProbePreservesIntrinsicEnabledStates),
            ("Probe can be cancelled from the dialog", TestProbeCanBeCancelledFromDialog),
            ("Probe latency source follows freshness", TestProbeLatencySourceFollowsFreshness)
        })
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void TestApplyPreservesIntrinsicEnabledStates()
    {
        var applyCalls = 0;
        using var dialog = CreateDialog(
            applyAsync: _ =>
            {
                applyCalls++;
                return Task.FromResult(true);
            },
            runProbeAsync: _ => Task.CompletedTask);

        var successPercent = GetField<NumericUpDown>(dialog, "_successPercent");
        successPercent.Value++;
        var before = CaptureEnabledState(dialog);

        InvokeTask(dialog, "CommitAsync", false);

        Assert(applyCalls == 1, "Apply delegate was not invoked exactly once.");
        AssertEnabledStatePreserved(dialog, before);
    }

    private static void TestProbePreservesIntrinsicEnabledStates()
    {
        var probeCalls = 0;
        using var dialog = CreateDialog(
            applyAsync: _ => Task.FromResult(true),
            runProbeAsync: _ =>
            {
                probeCalls++;
                return Task.CompletedTask;
            });
        var before = CaptureEnabledState(dialog);

        InvokeTask(dialog, "RunProbeAsync");

        Assert(probeCalls == 1, "Probe delegate was not invoked exactly once.");
        AssertEnabledStatePreserved(dialog, before);
    }

    private static void TestProbeCanBeCancelledFromDialog()
    {
        using var started = new ManualResetEventSlim();
        CancellationToken receivedToken = default;
        using var dialog = CreateDialog(
            applyAsync: _ => Task.FromResult(true),
            runProbeAsync: cancellationToken =>
            {
                receivedToken = cancellationToken;
                started.Set();
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var before = CaptureEnabledState(dialog);
        var cancelButton = GetField<Button>(dialog, "_cancelButton");

        var probeTask = StartTask(dialog, "RunProbeAsync");
        Assert(started.Wait(TimeSpan.FromSeconds(2)), "Probe delegate did not start.");
        Assert(cancelButton.Text == "取消检查" && cancelButton.Enabled,
            "The dialog did not expose an enabled cancel-probe command while probing.");

        RaiseClick(cancelButton);
        Assert(receivedToken.IsCancellationRequested,
            "The probe delegate did not receive cancellation from the dialog.");
        WaitWithMessagePump(probeTask, TimeSpan.FromSeconds(2));
        Assert(cancelButton.Text == "取消" && cancelButton.DialogResult == DialogResult.Cancel,
            "The dialog cancel button did not restore its normal close behavior.");
        AssertEnabledStatePreserved(dialog, before);
    }

    private static void TestProbeLatencySourceFollowsFreshness()
    {
        var provider = new ProviderStatus
        {
            FirstTokenLatencyMs = 2_000,
            ActiveProbeFirstTokenLatencyMs = 100,
            ActiveProbeSampleCount = 3
        };
        var expired = new ProviderGridRow
        {
            Source = provider,
            UsesActiveProbeLatency = false
        };
        Assert(expired.FirstTokenSource == "运营商上报",
            "An expired local probe was still presented as the active TTFT source.");

        var fresh = new ProviderGridRow
        {
            Source = provider,
            UsesActiveProbeLatency = true
        };
        Assert(fresh.FirstToken == "100 ms" &&
            fresh.FirstTokenSource == "本机中位数 100 ms / 共 3 次探测",
            "A fresh local probe was not presented as the active TTFT source.");
    }

    private static RoutingSettingsDialog CreateDialog(
        Func<RoutingSettingsDraft, Task<bool>> applyAsync,
        Func<CancellationToken, Task> runProbeAsync)
    {
        return new RoutingSettingsDialog(
            new RoutingUiSettings
            {
                MinimumSuccessPercent = 75,
                PollingIntervalSeconds = 60,
                AccountCacheSeconds = 300,
                AutoRoute = true,
                DurationCategory = TaskDurationCategory.Medium,
                BalancedSoftDeadlineSeconds = 5,
                BalancedExpectedOutputTokens = 1_000,
                ActiveProbeEnabled = true,
                ActiveProbeKeyId = 42,
                ActiveProbeModel = "synthetic-model"
            },
            "synthetic-api-key",
            NativeThemeManager.LightPalette,
            balancedMode: false,
            applyAsync,
            runProbeAsync);
    }

    private static EnabledState CaptureEnabledState(RoutingSettingsDialog dialog)
    {
        var state = new EnabledState(
            GetField<TabControl>(dialog, "_tabs").Enabled,
            GetField<CheckBox>(dialog, "_autoRoute").Enabled,
            GetField<NumericUpDown>(dialog, "_softDeadline").Enabled,
            GetField<NumericUpDown>(dialog, "_probeKeyId").Enabled);

        Assert(state.Tabs, "Tabs must start enabled.");
        Assert(state.AutoRoute, "Auto-route must start enabled.");
        Assert(!state.SoftDeadline, "Soft deadline must start disabled outside Balanced mode.");
        Assert(state.ProbeKeyId, "Probe Key ID must start enabled when probing is enabled.");
        return state;
    }

    private static void AssertEnabledStatePreserved(RoutingSettingsDialog dialog, EnabledState expected)
    {
        var actual = new EnabledState(
            GetField<TabControl>(dialog, "_tabs").Enabled,
            GetField<CheckBox>(dialog, "_autoRoute").Enabled,
            GetField<NumericUpDown>(dialog, "_softDeadline").Enabled,
            GetField<NumericUpDown>(dialog, "_probeKeyId").Enabled);

        Assert(actual == expected,
            $"Enabled state changed: expected {expected}, actual {actual}.");
    }

    private static T GetField<T>(object target, string name) where T : class
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {name} was not found.");
        return field.GetValue(target) as T
            ?? throw new InvalidOperationException($"Field {name} was not a {typeof(T).Name}.");
    }

    private static void InvokeTask(object target, string name, params object?[] arguments)
    {
        StartTask(target, name, arguments).GetAwaiter().GetResult();
    }

    private static Task StartTask(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {name} was not found.");
        return method.Invoke(target, arguments) as Task
            ?? throw new InvalidOperationException($"Method {name} did not return a Task.");
    }

    private static void RaiseClick(Button button)
    {
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Button.OnClick was not found.");
        onClick.Invoke(button, [EventArgs.Empty]);
    }

    private static void WaitWithMessagePump(Task task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }

        Assert(task.IsCompleted, "The probe task did not complete after cancellation.");
        task.GetAwaiter().GetResult();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private readonly record struct EnabledState(
        bool Tabs,
        bool AutoRoute,
        bool SoftDeadline,
        bool ProbeKeyId);
}
