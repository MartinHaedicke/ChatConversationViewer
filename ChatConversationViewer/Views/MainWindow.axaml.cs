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

    public MainWindow()
    {
        InitializeComponent();
        DetailWebView.AdapterCreated += (_, _) => _webViewReady = true;
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
        // before the native adapter exists (initial DataContextChanged fires before the
        // window is shown) nothing can be navigated; about:blank plus the empty-state
        // overlay covers that initial state, so skip silently
        if (!_webViewReady)
            return;

        DetailWebView.NavigateToString(ConversationHtmlBuilder.ToHtml(entries, title));
    }
}
