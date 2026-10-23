using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ResxStudio.Models;

namespace ResxStudio.ViewModels;

/// <summary>One language column: the default (neutral) file or a culture file.</summary>
public sealed class LanguageViewModel : ObservableObject
{
    public const string NeutralId = "neutral";

    private bool _isVisible = true;
    private bool _isDirty;
    private int _translated;
    private int _total;

    public LanguageViewModel(string? cultureCode, ResxDocument document)
    {
        CultureCode = cultureCode;
        Document = document;
        Id = cultureCode ?? NeutralId;

        if (cultureCode == null)
        {
            EnglishName = "Default";
            NativeName = "Default";
        }
        else
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(cultureCode);
                EnglishName = culture.EnglishName;
                NativeName = culture.NativeName;
            }
            catch (CultureNotFoundException)
            {
                EnglishName = NativeName = cultureCode;
            }
        }
    }

    /// <summary>Key used to look values up in an entry: the culture code, or "neutral".</summary>
    public string Id { get; }
    public string? CultureCode { get; }
    public bool IsNeutral => CultureCode == null;

    /// <summary>Replaced after a merge, when the file is re-read from disk.</summary>
    public ResxDocument Document { get; set; }

    /// <summary>The string values as they were on disk at the last load or save. Used for merging.</summary>
    public Dictionary<string, string> Baseline { get; set; } = new(StringComparer.Ordinal);

    public string EnglishName { get; }
    public string NativeName { get; }
    public string FileName => Path.GetFileName(Document.FilePath);

    public string ChipName => IsNeutral ? "Default" : EnglishName;
    public string Header => IsNeutral ? "Default" : $"{EnglishName} ({CultureCode})";

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => Set(ref _isDirty, value);
    }

    public double Progress => IsNeutral ? 100 : _total == 0 ? 0 : _translated * 100.0 / _total;

    public string ProgressText => IsNeutral ? $"{_total:N0} keys" : $"{Math.Floor(Progress):0}%";

    public string ProgressTooltip => IsNeutral
        ? $"{FileName}: the fallback text used when a translation is missing. Click to show or hide."
        : $"{FileName}: {_translated:N0} of {_total:N0} keys translated. Click to show or hide.";

    public void SetStats(int translated, int total)
    {
        if (_translated == translated && _total == total) return;
        _translated = translated;
        _total = total;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressTooltip));
    }
}
