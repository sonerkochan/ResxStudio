using System.Collections.Generic;
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
}

/// <summary>A problem type in the left column of the Problems panel.</summary>
public sealed class ProblemCategoryViewModel
{
    public ProblemCategoryViewModel(string name, IssueSeverity severity, IReadOnlyList<IssueViewModel> issues)
    {
        Name = name;
        Severity = severity;
        Issues = issues;
    }

    public string Name { get; }
    public IssueSeverity Severity { get; }
    public IReadOnlyList<IssueViewModel> Issues { get; }
    public int Count => Issues.Count;
}
