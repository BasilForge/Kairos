using System.ComponentModel;

namespace Kairos.Ui.Edit;

/// <summary>
/// One of the clip's own sound tracks in the editor's Sound section: its icon and name, with the clip's level and switch
/// or (a part row) the chosen parts' own level. A one-track clip's part row is its whole sound.
/// </summary>
public sealed class SoundTrackView : INotifyPropertyChanged
{
    readonly EditorViewModel _vm;
    readonly int _track;
    readonly bool _part;

    internal SoundTrackView(EditorViewModel vm, int track, string name, string glyph, bool part)
    {
        _vm = vm;
        _track = track;
        _part = part;
        Name = name;
        Glyph = glyph;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string Glyph { get; }

    /// <summary>The whole clip's row turns its track on and off; a part's shows only its icon.</summary>
    public bool HasSwitch => !_part;

    /// <summary>The level can be set: always for a part, while on for the clip.</summary>
    public bool Enabled => _part || On;

    /// <summary>The clip's switch: heard (on) or muted.</summary>
    public bool On
    {
        get => !_vm.TrackMuted(_track);
        set => _vm.SetTrackMuted(_track, !value);
    }

    public double Level
    {
        get => _part ? _vm.PartTrackLevel(_track) : _vm.TrackLevel(_track);
        set
        {
            if (_part)
                _vm.SetPartTrackLevel(_track, value);
            else
                _vm.SetTrackLevel(_track, value);
        }
    }

    /// <summary>The level as a percent (a muted track's 0%).</summary>
    public string Label => EditorViewModel.Percent(_part || On ? Level : 0);

    /// <summary>Its name and level, where the row has no room to show them (two columns).</summary>
    public string Tip => $"{Name}  {Label}";

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
