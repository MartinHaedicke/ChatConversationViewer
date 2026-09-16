using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using ChatConversationViewer.Models;
using ChatConversationViewer.ViewModels;

namespace ChatConversationViewer.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private bool _webViewReady;
    private string? _pendingHtml;

    public MainWindow()
    {
        InitializeComponent();
        DetailWebView.AdapterCreated += (_, _) =>
        {
            _webViewReady = true;
            // a render may have been requested before the adapter existed —
            // the webview is invisible until a session is selected, so its
            // adapter can be created lazily at that moment
            if (_pendingHtml is { } html)
            {
                _pendingHtml = null;
                DetailWebView.NavigateToString(html);
            }
        };
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is { } old)
            old.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not { } vm)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        Render(vm.Entries, vm.CurrentSession?.Title);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
            return;

        switch (e.PropertyName)
        {
            // Entries is replaced on session selection (empty first, loaded later),
            // on the sidechain toggle (ApplyFilter) and on refresh — rerender each time
            case nameof(MainWindowViewModel.Entries):
                Render(vm.Entries, vm.CurrentSession?.Title);
                break;

            case nameof(MainWindowViewModel.CurrentSession) when vm.CurrentSession is null:
                Render(Array.Empty<ConversationEntry>(), null);
                break;
        }
    }

    private void Render(IReadOnlyList<ConversationEntry> entries, string? title)
    {
        // nothing selected: the webview is hidden via the IsVisible binding and the
        // overlay text covers the empty state — no document to build or navigate
        if (title is null && entries.Count == 0)
        {
            _pendingHtml = null;
            return;
        }

        var html = ConversationHtmlBuilder.ToHtml(entries, title);

        if (_webViewReady)
            DetailWebView.NavigateToString(html);
        else
            _pendingHtml = html;
    }
}
