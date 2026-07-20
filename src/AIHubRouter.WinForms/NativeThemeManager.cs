using System.Drawing;
using AIHubRouter.Core;
using Microsoft.Win32;

namespace AIHubRouter.WinForms;

internal sealed record NativeThemePalette(
    Color Window,
    Color Surface,
    Color Control,
    Color Text,
    Color MutedText,
    Color Border,
    Color Header,
    Color AlternateRow,
    Color Selection,
    Color SelectionText,
    Color Success,
    Color Error);

internal static class NativeThemeManager
{
    public static NativeThemePalette LightPalette { get; } = new(
        Color.FromArgb(245, 247, 249),
        Color.White,
        Color.White,
        Color.FromArgb(35, 43, 52),
        Color.FromArgb(75, 85, 95),
        Color.FromArgb(225, 229, 234),
        Color.FromArgb(240, 243, 246),
        Color.FromArgb(249, 250, 251),
        Color.FromArgb(220, 236, 248),
        Color.FromArgb(24, 64, 90),
        Color.FromArgb(20, 110, 65),
        Color.FromArgb(185, 45, 45));

    public static NativeThemePalette DarkPalette { get; } = new(
        Color.FromArgb(30, 32, 35),
        Color.FromArgb(40, 43, 47),
        Color.FromArgb(50, 54, 59),
        Color.FromArgb(234, 237, 240),
        Color.FromArgb(178, 185, 193),
        Color.FromArgb(72, 77, 84),
        Color.FromArgb(54, 58, 63),
        Color.FromArgb(45, 48, 52),
        Color.FromArgb(62, 91, 112),
        Color.White,
        Color.FromArgb(88, 201, 139),
        Color.FromArgb(245, 120, 120));

    public static NativeThemePalette Resolve(WinFormsTheme preference)
    {
        return preference switch
        {
            WinFormsTheme.Light => LightPalette,
            WinFormsTheme.Dark => DarkPalette,
            _ => SystemUsesLightTheme() ? LightPalette : DarkPalette
        };
    }

    public static void Apply(Control root, NativeThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(palette);
        ApplyControl(root, palette);
        root.Invalidate(true);
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void ApplyControl(Control control, NativeThemePalette palette)
    {
        control.ForeColor = palette.Text;
        control.BackColor = control switch
        {
            Form or Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer => palette.Window,
            GroupBox => palette.Surface,
            TextBoxBase or ComboBox or NumericUpDown => palette.Control,
            Button => palette.Control,
            StatusStrip or ToolStrip => palette.Surface,
            _ => control.Parent is GroupBox ? palette.Surface : palette.Window
        };

        if (control is Button button)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = palette.Border;
        }

        if (control is DataGridView grid)
        {
            grid.BackgroundColor = palette.Surface;
            grid.GridColor = palette.Border;
            grid.DefaultCellStyle.BackColor = palette.Surface;
            grid.DefaultCellStyle.ForeColor = palette.Text;
            grid.DefaultCellStyle.SelectionBackColor = palette.Selection;
            grid.DefaultCellStyle.SelectionForeColor = palette.SelectionText;
            grid.AlternatingRowsDefaultCellStyle.BackColor = palette.AlternateRow;
            grid.ColumnHeadersDefaultCellStyle.BackColor = palette.Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Text;
        }

        if (control is ToolStrip strip)
        {
            foreach (ToolStripItem item in strip.Items)
            {
                item.BackColor = palette.Surface;
                item.ForeColor = palette.Text;
            }
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, palette);
        }
    }
}
