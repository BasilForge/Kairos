using System.Windows;
using System.Windows.Input;
using Kairos.App;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- saving (the editor stays until it is written)

    bool _saving, _saveDone, _saveReplace;
    double _saveProgress;
    string _saveError = "";
    Action? _cancelSave;

    /// <summary>The edit is being written: the editor shows its progress and takes nothing else.</summary>
    public bool IsSaving => _saving;
    public double SaveProgress => _saveProgress;
    public string SavePercent => (int)Math.Floor(_saveProgress * 100) + "%";
    /// <summary>Saving, saved, or not saved.</summary>
    public string SaveTitle => Text.Get(_saveError.Length > 0 ? "editor.notSaved" : _saveDone ? "editor.savedTitle" : "editor.savingTitle");
    /// <summary>How it is saved: as a new copy, or in place of the original.</summary>
    public string SaveNote => Text.Get(_saveReplace ? "editor.savingReplace" : "editor.savingCopy");
    /// <summary>Written: the progress is full and shows a check for a moment before the editor goes.</summary>
    public bool SaveDone => _saveDone;
    public string SaveError => _saveError;
    public bool HasSaveError => _saveError.Length > 0;
    /// <summary>Still writing (the percentage and Cancel show).</summary>
    public bool SaveWorking => _saving && !_saveDone && _saveError.Length == 0;
    public ICommand CancelSaveCommand { get; }
    public ICommand BackToEditingCommand { get; }

    public void BeginSaving(bool replace, Action cancel)
    {
        (_saving, _saveDone, _saveReplace, _saveProgress, _saveError, _cancelSave) = (true, false, replace, 0, "", cancel);
        RaiseSaving();
    }

    public void ReportSaving(double progress)
    {
        _saveProgress = Math.Clamp(progress, 0, 1);
        RaiseSaving();
    }

    public void FinishSaving()
    {
        (_saveDone, _saveProgress) = (true, 1);
        RaiseSaving();
    }

    public void FailSaving(string why)
    {
        _saveError = Text.Format("editor.failed", why);
        RaiseSaving();
    }

    /// <summary>Back to editing (after a cancel or a failure), or the save is over.</summary>
    public void EndSaving()
    {
        (_saving, _saveDone, _saveError, _cancelSave) = (false, false, "", null);
        RaiseSaving();
    }

    void RaiseSaving()
    {
        foreach (string name in new[] { nameof(IsSaving), nameof(SaveProgress), nameof(SavePercent), nameof(SaveNote), nameof(SaveDone),
                     nameof(SaveError), nameof(HasSaveError), nameof(SaveWorking), nameof(SaveTitle) })
            Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>A finished change: one step to undo.</summary>
    public void Apply(EditProject next)
    {
        _lastAmend = null;
        if (!ReferenceEquals(next, Project))
            _history.Push(next);
    }

    /// <summary>A change still in progress (a slider): repeated ones of the same kind are one step to undo.</summary>
    public void Amend(string kind, EditProject next)
    {
        if (_lastAmend == kind)
            _history.Amend(next);
        else
            _history.Push(next);
        _lastAmend = kind;
    }

    /// <summary>
    /// Texts left without words (a new one whose words were to be typed in the inspector, and were not) go, each with
    /// the step that made it.
    /// </summary>
    public void DropEmptyTexts()
    {
        foreach (var text in Project.Items.OfType<TextItem>().Where(t => t.Text.Length == 0).ToList())
            Amend("text:" + text.Id, Project.Without(text.Id));
    }

    /// <summary>Free removes the crop; a ratio takes the largest centred box of that shape.</summary>
    void Crop(string ratio)
    {
        if (ratio == "free")
        {
            Apply(Project with { Crop = null });
            return;
        }
        double target = ratio switch { "16:9" => 16 / 9.0, "9:16" => 9 / 16.0, _ => 1.0 };
        // In 0..1 of each side: width share w and height share h with (w * aspect) / h == target.
        double w = 1, h = _aspect / target;
        if (h > 1)
        {
            w = target / _aspect;
            h = 1;
        }
        Apply(Project with { Crop = new Rect((1 - w) / 2, (1 - h) / 2, w, h) });
    }

}
