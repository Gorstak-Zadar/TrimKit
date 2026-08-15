namespace TrimKit.Services;

/// <summary>
/// Abstraction over UI dialogs (MessageBox, file/folder pickers) to decouple
/// ViewModels from WPF types and improve testability.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a confirmation dialog with Yes/No buttons.
    /// Returns true if user clicked Yes.
    /// </summary>
    bool Confirm(string message, string title);

    /// <summary>
    /// Shows a warning message dialog (OK only).
    /// </summary>
    void ShowWarning(string message, string title);

    /// <summary>
    /// Shows an error message dialog (OK only).
    /// </summary>
    void ShowError(string message, string title);

    /// <summary>
    /// Shows a Yes/No/Cancel dialog for unmount-retry scenarios.
    /// Returns: true = Yes (retry), false = No (discard), null = Cancel.
    /// </summary>
    bool? ConfirmWithCancel(string message, string title);

    /// <summary>
    /// Shows an Open File dialog. Returns the selected file path, or null if cancelled.
    /// </summary>
    string? OpenFile(string filter, string title, bool multiSelect = false);

    /// <summary>
    /// Shows an Open File dialog with multi-select. Returns selected paths, or empty if cancelled.
    /// </summary>
    string[]? OpenFiles(string filter, string title);

    /// <summary>
    /// Shows a Save File dialog. Returns the selected path, or null if cancelled.
    /// </summary>
    string? SaveFile(string filter, string title, string? defaultFileName = null);

    /// <summary>
    /// Shows a folder picker dialog. Returns the selected folder path, or null if cancelled.
    /// </summary>
    string? OpenFolder(string title, string? initialDirectory = null);
}
