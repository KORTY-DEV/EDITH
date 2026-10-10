using System.IO;
using Edith.Core;
using Forms = System.Windows.Forms;

namespace Edith.App;

/// <summary>
/// EDITH's icon in the notification area: state in the tooltip, a menu to talk, open the
/// status panel and settings, restart and exit. Double click opens the status panel.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _stateItem;

    public TrayIcon(Action talk, Action statusPanel, Action settings, Action restart, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        _stateItem = new Forms.ToolStripMenuItem("Жду «Эдит»") { Enabled = false };
        menu.Items.Add(_stateItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Поговорить (Ctrl+Alt+E)", null, (_, _) => talk());
        menu.Items.Add("Панель состояния (Ctrl+Alt+J)", null, (_, _) => statusPanel());
        menu.Items.Add("Настройки…", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Перезапустить", null, (_, _) => restart());
        menu.Items.Add("Выход (Ctrl+Alt+Shift+E)", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "E.D.I.T.H. — жду «Эдит»",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => statusPanel();
    }

    /// <summary>A checkable item above the separator before "Перезапустить".</summary>
    public void AddToggle(string text, Func<bool> isOn, Action<bool> set)
    {
        var item = new Forms.ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) => set(!isOn());
        Forms.ContextMenuStrip menu = _icon.ContextMenuStrip!;
        menu.Items.Insert(menu.Items.Count - 3, item);
        menu.Opening += (_, _) => item.Checked = isOn();
    }

    public void SetState(AssistantState state)
    {
        string text = state switch
        {
            AssistantState.Listening => "Слушаю",
            AssistantState.Thinking => "Думаю",
            AssistantState.Speaking => "Говорю",
            AssistantState.Warning => "Что-то не так — см. панель",
            _ => "Жду «Эдит»",
        };
        _stateItem.Text = text;
        _icon.Text = "E.D.I.T.H. — " + text.ToLowerInvariant();
    }

    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "edith.ico");
        try
        {
            if (File.Exists(path))
                return new System.Drawing.Icon(path, System.Windows.Forms.SystemInformation.SmallIconSize);
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } own)
                return own;
        }
        catch
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
