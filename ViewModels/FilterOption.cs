namespace ResxStudio.ViewModels;

public enum EntryFilter { All, Untranslated, AutoTranslated, WithProblems }

public sealed record FilterOption(EntryFilter Mode, string Label);
