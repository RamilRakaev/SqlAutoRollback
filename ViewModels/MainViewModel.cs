using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlAutoRollback.Models;
using SqlAutoRollback.Services;

namespace SqlAutoRollback.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISqlExecutionService _sql;
    private readonly IScriptFileService _files;
    private readonly IHistoryService _history;
    private readonly IDialogService _dialogs;
    private readonly ICredentialStore _credentials;
    private int _queryCounter;

    public MainViewModel(
        ISqlExecutionService sql,
        IScriptFileService files,
        IHistoryService history,
        IDialogService dialogs,
        ICredentialStore credentials)
    {
        _sql = sql;
        _files = files;
        _history = history;
        _dialogs = dialogs;
        _credentials = credentials;

        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConnections));
        ResultTables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResultTables));
        HistoryEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        Documents.CollectionChanged += (_, _) =>
        {
            SaveAllCommand.NotifyCanExecuteChanged();
        };

        NewQuery();
    }

    public bool HasConnections => Connections.Count > 0;

    public bool HasResultTables => ResultTables.Count > 0;

    public bool HasHistory => HistoryEntries.Count > 0;

    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];

    public ObservableCollection<QueryDocumentViewModel> Documents { get; } = [];

    public ObservableCollection<DataTable> ResultTables { get; } = [];

    public ObservableCollection<ScriptHistoryEntry> HistoryEntries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private ConnectionItemViewModel? activeConnection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseDocumentCommand))]
    private QueryDocumentViewModel? activeDocument;

    [ObservableProperty]
    private string resultMessage = string.Empty;

    [ObservableProperty]
    private bool hasExecutionError;

    [ObservableProperty]
    private int selectedBottomTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool isExecuting;

    partial void OnActiveConnectionChanged(ConnectionItemViewModel? value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        _ = ReloadHistoryAsync();
    }

    [RelayCommand]
    private void ShowConnect()
    {
        var lastUsed = _credentials.Load();
        var result = _dialogs.ShowConnectDialog(lastUsed);
        if (result is null)
        {
            return;
        }

        _credentials.Save(result.Credentials);
        var connectionString = SqlConnectionFactory.Build(result.Credentials);
        var displayName = string.IsNullOrWhiteSpace(result.ProductMajorVersion)
            ? result.Credentials.ServerName
            : $"{result.Credentials.ServerName} (SQL Server {result.ProductMajorVersion})";

        var existing = Connections.FirstOrDefault(item =>
            string.Equals(item.ServerName, result.Credentials.ServerName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.ConnectionString = connectionString;
            existing.UserName = result.Credentials.UserName;
            existing.DisplayName = displayName;
            if (ReferenceEquals(ActiveConnection, existing))
            {
                _ = ReloadHistoryAsync();
            }
            else
            {
                ActiveConnection = existing;
            }

            return;
        }

        var connection = new ConnectionItemViewModel
        {
            ServerName = result.Credentials.ServerName,
            DisplayName = displayName,
            UserName = result.Credentials.UserName,
            ConnectionString = connectionString
        };

        Connections.Add(connection);
        ActiveConnection = connection;
    }

    [RelayCommand]
    private void OpenFile()
    {
        var path = _dialogs.ShowOpenSqlFileDialog();
        if (path is null)
        {
            return;
        }

        try
        {
            var existingDocument = Documents.FirstOrDefault(document =>
                PathsEqual(document.FilePath, path));
            if (existingDocument is not null)
            {
                ActiveDocument = existingDocument;
                return;
            }

            var text = _files.Read(path);
            var document = new QueryDocumentViewModel
            {
                Title = Path.GetFileName(path),
                FilePath = path
            };
            document.SetText(text);
            Documents.Add(document);
            ActiveDocument = document;
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, "Open File");
        }
    }

    [RelayCommand]
    private void NewQuery()
    {
        _queryCounter++;
        var document = new QueryDocumentViewModel
        {
            Title = $"SQLQuery{_queryCounter}.sql"
        };
        document.SetText(string.Empty);
        Documents.Add(document);
        ActiveDocument = document;
    }

    private bool CanSaveQuery() => ActiveDocument is not null;

    [RelayCommand(CanExecute = nameof(CanSaveQuery))]
    private void SaveQuery()
    {
        if (ActiveDocument is null)
        {
            return;
        }

        SaveDocument(ActiveDocument);
    }

    private bool CanSaveAll() => Documents.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private void SaveAll()
    {
        foreach (var document in Documents.ToList())
        {
            SaveDocument(document);
        }
    }

    private bool CanRunQuery() => ActiveConnection is not null && ActiveDocument is not null && !IsExecuting;

    [RelayCommand(CanExecute = nameof(CanRunQuery))]
    private async Task ExecuteAsync()
    {
        if (ActiveConnection is null || ActiveDocument is null)
        {
            return;
        }

        var script = ActiveDocument.Text;
        if (string.IsNullOrWhiteSpace(script))
        {
            ResultTables.Clear();
            HasExecutionError = false;
            ResultMessage = "Nothing to execute.";
            SelectedBottomTab = 0;
            return;
        }

        IsExecuting = true;
        ExecuteCommand.NotifyCanExecuteChanged();
        ResultTables.Clear();
        HasExecutionError = false;
        ResultMessage = "Executing...";
        SelectedBottomTab = 0;

        try
        {
            var result = await _sql.ExecuteAsync(ActiveConnection.ConnectionString, script);
            ResultTables.Clear();
            foreach (var table in result.Tables)
            {
                ResultTables.Add(table);
            }

            HasExecutionError = !result.Success;
            ResultMessage = result.Message;

            if (result.Success)
            {
                await _history.AddAsync(ActiveConnection.ServerName, script, result.RowsAffected);
                await ReloadHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            HasExecutionError = true;
            ResultMessage = ex.Message;
        }
        finally
        {
            IsExecuting = false;
            ExecuteCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CloseDocument(QueryDocumentViewModel? document)
    {
        var target = document ?? ActiveDocument;
        if (target is null)
        {
            return;
        }

        var index = Documents.IndexOf(target);
        Documents.Remove(target);

        if (ActiveDocument == target)
        {
            ActiveDocument = Documents.Count == 0
                ? null
                : Documents[Math.Clamp(index - 1, 0, Documents.Count - 1)];
        }

        SaveAllCommand.NotifyCanExecuteChanged();
        SaveQueryCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void SaveDocument(QueryDocumentViewModel document)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(document.FilePath))
            {
                _files.Save(document.FilePath, document.Text);
            }
            else
            {
                var path = _files.SaveNew(document.Text);
                document.FilePath = path;
                document.Title = Path.GetFileName(path);
            }

            document.IsDirty = false;
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, "Save");
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task ReloadHistoryAsync()
    {
        var connection = ActiveConnection;
        HistoryEntries.Clear();
        if (connection is null)
        {
            return;
        }

        try
        {
            var entries = await _history.GetAsync(connection.ServerName);
            if (!ReferenceEquals(ActiveConnection, connection))
            {
                return;
            }

            HistoryEntries.Clear();
            foreach (var entry in entries)
            {
                HistoryEntries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(ActiveConnection, connection))
            {
                return;
            }

            ResultMessage = string.IsNullOrWhiteSpace(ResultMessage)
                ? ex.Message
                : ResultMessage;
        }
    }
}
