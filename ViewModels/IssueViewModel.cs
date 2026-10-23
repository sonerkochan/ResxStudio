using ResxStudio.Services;

namespace ResxStudio.ViewModels;

/// <summary>One row in the Problems list.</summary>
public sealed class IssueViewModel
{
    public IssueViewModel(ValidationIssue issue) => Issue = issue;

    public ValidationIssue Issue { get; }
    public IssueSeverity Severity => Issue.Severity;
    public string Category => Issue.Category;
    public string Key => Issue.Key;
    public string Location => Issue.Location;
    public string Message => Issue.Message;

    /// <summary>Errors and warnings start expanded; long informational lists start collapsed.</summary>
    public bool ExpandByDefault => Issue.Severity != IssueSeverity.Info;
}
