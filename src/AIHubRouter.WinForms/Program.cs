namespace AIHubRouter.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => ShowFatalError(eventArgs.Exception);

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception exception)
        {
            ShowFatalError(exception);
        }
    }

    private static void ShowFatalError(Exception exception)
    {
        MessageBox.Show(
            $"程序遇到无法恢复的错误。\n\n{exception.Message}",
            "AIHub 最低价路由器",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
