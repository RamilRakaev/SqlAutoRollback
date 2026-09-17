using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;

namespace SqlAutoRollback.ViewModels;

public sealed partial class QueryDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = "SQLQuery1.sql";

    [ObservableProperty]
    private string? filePath;

    [ObservableProperty]
    private bool isDirty;

    public string TabTitle => IsDirty ? $"{Title}*" : Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(TabTitle));

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(TabTitle));

    public QueryDocumentViewModel()
    {
        Document.TextChanged += (_, _) => IsDirty = true;
    }

    public TextDocument Document { get; } = new();

    public string Text => Document.Text;

    public void SetText(string text)
    {
        Document.Text = text ?? string.Empty;
        IsDirty = false;
    }
}
