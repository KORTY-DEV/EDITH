using Edith.App;

namespace Edith.App.Tests;

[Collection("LibrarianSwitch")]
public sealed class SettingSyncTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "edith-switch-" + Guid.NewGuid().ToString("N") + ".txt");
    private readonly string _previous = LibrarianSwitch.StorePath;

    public SettingSyncTests() => LibrarianSwitch.StorePath = _file;

    public void Dispose()
    {
        LibrarianSwitch.StorePath = _previous;
        LibrarianSwitch.Reload();
        try { File.Delete(_file); } catch { }
    }

    [Theory]
    [InlineData(true, true, true, true)]    // untouched, still on
    [InlineData(false, true, true, false)]  // switched off elsewhere, window untouched: stays off
    [InlineData(true, false, false, true)]  // switched on elsewhere, window untouched: stays on
    [InlineData(false, false, true, true)]  // the owner ticked it: on
    [InlineData(true, true, false, false)]  // the owner unticked it: off
    public void Resolve_KeepsWhatChangedElsewhere_UnlessTheOwnerTouchedTheBox(bool nowOutside, bool loaded, bool shown, bool expected) =>
        Assert.Equal(expected, SettingSync.Resolve(nowOutside, loaded, shown));

    [Fact]
    public void LibrarianSwitch_SurvivesSavingASettingsWindowThatWasOpenedBeforeTheTrayToggle()
    {
        LibrarianSwitch.Set(true);
        bool loadedIntoWindow = LibrarianSwitch.On;   // the settings tab was opened while it was on...
        bool shownInWindow = loadedIntoWindow;        // ...and the owner did not touch the box

        LibrarianSwitch.Set(false);                   // the tray switched it off meanwhile

        bool wanted = SettingSync.Resolve(LibrarianSwitch.On, loadedIntoWindow, shownInWindow);
        if (wanted != LibrarianSwitch.On)
            LibrarianSwitch.Set(wanted);              // what Save does

        Assert.False(LibrarianSwitch.On);
        LibrarianSwitch.Reload();
        Assert.False(LibrarianSwitch.On);             // and it is remembered on disk
    }

    [Fact]
    public void LibrarianSwitch_DefaultsToOff_AndRemembersTheOwnersChoice()
    {
        LibrarianSwitch.Reload();
        Assert.False(LibrarianSwitch.On);

        LibrarianSwitch.Set(true);
        LibrarianSwitch.Reload();

        Assert.True(LibrarianSwitch.On);
    }

    [Theory]
    [InlineData("включи библиотекаря", true)]
    [InlineData("выключи библиотекаря", false)]
    public void VoiceCommands_FlipTheSwitch(string phrase, bool expected)
    {
        LibrarianSwitch.Set(!expected);

        Assert.True(LibrarianSwitch.TryHandle(phrase, out _));

        Assert.Equal(expected, LibrarianSwitch.On);
    }
}
