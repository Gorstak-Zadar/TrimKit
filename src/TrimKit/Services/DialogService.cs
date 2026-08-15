using System.Windows;
using Microsoft.Win32;

namespace TrimKit.Services;

/// <summary>
/// WPF implementation of IDialogService using Microsoft.Win32 dialogs
/// (no WinForms dependency needed — uses OpenFolderDialog from .NET 8+).
/// </summary>
public class DialogService : IDialogService
{
    public bool Confirm(string message, string title)
    {
        var result = MessageBox.Show(message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void ShowWarning(string message, string title)
    {
        MessageBox.Show(message, title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void ShowError(string message, string title)
    {
        MessageBox.Show(message, title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool? ConfirmWithCancel(string message, string title)
    {
        var result = MessageBox.Show(message, title,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null
        };
    }

    public string? OpenFile(string filter, string title, bool multiSelect = false)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Title = title,
            Multiselect = multiSelect
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string[]? OpenFiles(string filter, string title)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Title = title,
            Multiselect = true
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string? SaveFile(string filter, string title, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            Title = title
        };

        if (!string.IsNullOrEmpty(defaultFileName))
            dialog.FileName = defaultFileName;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title
        };

        if (!string.IsNullOrEmpty(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
