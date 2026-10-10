namespace Edith.App;

/// <summary>
/// A settings window shows a snapshot taken when it was opened. Saving must not write that snapshot over what changed since
/// (the librarian switched in the tray, a mode switched by voice): only what the owner actually changed in the window counts.
/// </summary>
public static class SettingSync
{
    /// <summary>The value to keep: the checkbox if the owner touched it, otherwise whatever the setting is right now.</summary>
    public static bool Resolve(bool currentNow, bool loadedIntoWindow, bool shownInWindow) =>
        shownInWindow != loadedIntoWindow ? shownInWindow : currentNow;
}
