namespace ResxStudio.Services;

/// <summary>Result of validating text typed into an input dialog.</summary>
public readonly record struct InputCheck(bool IsValid, string Message);
