using System.Threading.Tasks;
using Avalonia.Controls;

namespace ChatConversationViewer.Views;

public partial class ExportOptionsDialog : Window
{
    public ExportOptionsDialog()
    {
        InitializeComponent();
        IncludeButton.Click += (_, _) => Close(true);
        ExcludeButton.Click += (_, _) => Close(false);
    }

    /// <summary>
    /// Asks whether nested subagent transcripts should be included in a Markdown export.
    /// Dismissing the dialog (Escape / closing it) is treated the same as "Exclude".
    /// </summary>
    public static async Task<bool> AskIncludeSubagentTranscriptsAsync(Window owner)
    {
        var dialog = new ExportOptionsDialog();
        return await dialog.ShowDialog<bool?>(owner) ?? false;
    }
}
