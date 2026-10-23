using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ResxStudio.Models;
using ResxStudio.Services;
using ResxStudio.ViewModels;

namespace ResxStudio.Views;

/// <summary>Fills missing translations with MyMemory, protecting placeholders and names.</summary>
public partial class AutofillDialog : Window
{
    private const int FailuresBeforeStopping = 5;

    private readonly AutofillContext _context;
    private readonly List<(CheckBox Box, LanguageViewModel Language)> _languageBoxes = new();
    private CancellationTokenSource? _cancellation;
    private bool _running;
    private bool _closeWhenStopped;

    private sealed record Job(EntryViewModel Entry, LanguageViewModel Language, string SourceText);

    public AutofillDialog(AutofillContext context)
    {
        InitializeComponent();
        _context = context;

        foreach (var language in context.Languages)
        {
            var box = new CheckBox
            {
                Content = language.Header,
                IsChecked = language.IsVisible,
                Margin = new Thickness(0, 0, 18, 6),
            };
            box.Checked += (_, _) => UpdateEstimate();
            box.Unchecked += (_, _) => UpdateEstimate();
            _languageBoxes.Add((box, language));
            LanguagesPanel.Children.Add(box);
        }

        ScopeSelected.IsEnabled = context.SelectedEntry != null;
        ScopeView.IsChecked = true;
        SourceBox.Text = context.Settings.SourceLanguage;
        EmailBox.Text = context.Settings.MyMemoryEmail ?? "";
        TermsBox.Text = string.Join(Environment.NewLine, context.Settings.ProtectedTerms);

        ScopeView.Checked += (_, _) => UpdateEstimate();
        ScopeAll.Checked += (_, _) => UpdateEstimate();
        ScopeSelected.Checked += (_, _) => UpdateEstimate();
        EmailBox.TextChanged += (_, _) => UpdateEstimate();
        StartButton.Click += OnStartClick;
        CloseButton.Click += (_, _) => Close();
        Closing += OnClosing;

        UpdateEstimate();
    }

    private bool HasEmail => EmailBox.Text.Contains('@');

    private List<string> ProtectedTerms() => TermsBox.Text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<Job> BuildJobs()
    {
        var languages = _languageBoxes.Where(x => x.Box.IsChecked == true).Select(x => x.Language).ToList();

        IEnumerable<EntryViewModel> entries;
        if (ScopeSelected.IsChecked == true && _context.SelectedEntry != null) entries = new[] { _context.SelectedEntry };
        else if (ScopeAll.IsChecked == true) entries = _context.AllEntries;
        else entries = _context.ViewEntries;

        var jobs = new List<Job>();
        foreach (var language in languages)
        {
            foreach (var entry in entries)
            {
                var source = entry[LanguageViewModel.NeutralId];
                if (string.IsNullOrWhiteSpace(source) || entry.HasValue(language.Id)) continue;
                jobs.Add(new Job(entry, language, source));
            }
        }
        return jobs;
    }

    private void UpdateEstimate()
    {
        if (_running) return;
        var jobs = BuildJobs();
        if (jobs.Count == 0)
        {
            EstimateText.Text = "Nothing to fill: the chosen keys already have text in the chosen languages.";
            StartButton.IsEnabled = false;
            return;
        }

        StartButton.IsEnabled = true;
        // Identical texts are translated once per language, so count them once.
        var characters = jobs
            .GroupBy(j => (j.Language.Id, j.SourceText))
            .Sum(g => g.Key.SourceText.Length);
        var limit = HasEmail ? 50_000 : 5_000;
        var days = (int)Math.Ceiling(characters / (double)limit);

        var texts = jobs.Count == 1 ? "1 text" : $"{jobs.Count:N0} texts";
        var withWhat = HasEmail ? "with your email address" : "without an email address";
        EstimateText.Text = days <= 1
            ? $"{texts}, about {characters:N0} characters. That fits in one day of MyMemory's free limit ({limit:N0} characters {withWhat})."
            : $"{texts}, about {characters:N0} characters. MyMemory's free limit is {limit:N0} characters a day {withWhat}, " +
              $"so this takes about {days} days. Each run fills what it can and keeps it; run it again the next day to continue.";
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cancellation?.Cancel();
            StartButton.IsEnabled = false;
            StatusText.Text = "Stopping after the current text…";
            return;
        }

        var source = SourceBox.Text.Trim();
        if (!ResourceSetLocator.IsCulture(source))
        {
            StatusText.Text = "Enter the language of the default file, for example en.";
            SourceBox.Focus();
            return;
        }

        var terms = ProtectedTerms();
        _context.Settings.SourceLanguage = source;
        _context.Settings.MyMemoryEmail = EmailBox.Text.Trim();
        _context.Settings.ProtectedTerms = terms;
        _context.Settings.Save();

        var jobs = BuildJobs();
        if (jobs.Count == 0) return;

        await RunAsync(jobs, MyMemoryClient.ToServiceCode(source), terms);
    }

    private async Task RunAsync(List<Job> jobs, string sourceCode, List<string> terms)
    {
        _running = true;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        SetInputsEnabled(false);
        StartButton.Content = "Stop";
        LogBox.Clear();
        LogBox.Visibility = Visibility.Collapsed;
        RunProgress.Maximum = jobs.Count;
        RunProgress.Value = 0;

        var client = new MyMemoryClient(EmailBox.Text);
        var translator = new MachineTranslator(client, terms);
        var cache = new Dictionary<(string, string), string>();
        int filled = 0, failed = 0, failuresInRow = 0;
        string? stopReason = null;

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            RunProgress.Value = i;
            if (token.IsCancellationRequested)
            {
                stopReason = "Stopped.";
                break;
            }
            if (job.Entry.HasValue(job.Language.Id)) continue;

            StatusText.Text = $"Translating {i + 1:N0} of {jobs.Count:N0} into {job.Language.EnglishName}: {job.Entry.Key}";
            try
            {
                var cacheKey = (job.Language.Id, job.SourceText);
                if (!cache.TryGetValue(cacheKey, out var translated))
                {
                    var target = MyMemoryClient.ToServiceCode(job.Language.CultureCode!);
                    translated = await translator.TranslateAsync(job.SourceText, sourceCode, target, token);
                    cache[cacheKey] = translated;
                }
                job.Entry.SetMachineTranslation(job.Language.Id, translated);
                filled++;
                failuresInRow = 0;
            }
            catch (TranslationQuotaException ex)
            {
                Log(ex.Message);
                stopReason = "MyMemory's daily limit is used up. Everything translated so far is kept; run this again tomorrow to continue.";
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stopReason = "Stopped.";
                break;
            }
            catch (Exception ex) when (ex is TranslationException or HttpRequestException or OperationCanceledException or JsonException)
            {
                failed++;
                failuresInRow++;
                var reason = ex is OperationCanceledException ? "the request timed out" : ex.Message;
                Log($"{job.Entry.Key} ({job.Language.CultureCode}): {reason}");
                if (failuresInRow >= FailuresBeforeStopping)
                {
                    stopReason = $"Stopped after {FailuresBeforeStopping} failures in a row. Check your internet connection and the language codes.";
                    break;
                }
            }
        }

        if (stopReason == null) RunProgress.Value = RunProgress.Maximum;

        var summary = $"Filled {Plural(filled, "translation")}";
        if (failed > 0) summary += $", {failed:N0} failed";
        summary += $". About {client.CharactersSent:N0} characters used.";
        if (filled > 0) summary += " Auto-translated text is highlighted in amber until you review it, and nothing is written to disk until you save.";
        if (stopReason != null) summary += " " + stopReason;
        StatusText.Text = summary;

        _running = false;
        _cancellation.Dispose();
        _cancellation = null;
        SetInputsEnabled(true);
        StartButton.Content = "Start";
        UpdateEstimate();

        if (_closeWhenStopped) Close();
    }

    private void SetInputsEnabled(bool enabled)
    {
        foreach (var (box, _) in _languageBoxes) box.IsEnabled = enabled;
        ScopeView.IsEnabled = enabled;
        ScopeAll.IsEnabled = enabled;
        ScopeSelected.IsEnabled = enabled && _context.SelectedEntry != null;
        SourceBox.IsEnabled = enabled;
        EmailBox.IsEnabled = enabled;
        TermsBox.IsEnabled = enabled;
        CloseButton.IsEnabled = enabled;
        StartButton.IsEnabled = true;
    }

    private void Log(string line)
    {
        LogBox.Visibility = Visibility.Visible;
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        // Let the current request finish so the last result isn't half-applied.
        e.Cancel = true;
        _closeWhenStopped = true;
        _cancellation?.Cancel();
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";
}
