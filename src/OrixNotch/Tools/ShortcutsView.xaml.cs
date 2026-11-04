using System.Windows.Media;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>8-column App Launcher grid with drag &amp; drop rearranging, session activation, and custom app management.</summary>
public partial class ShortcutsView : UserControl, IEscapeHandler
{
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ShortcutItem? _editing;
    private static ShortcutsService Service => ShortcutsService.Instance;

    private Point _dragStartPoint;
    private bool _isDraggingItem;
    private long _lastDragEndTime;

    public ShortcutsView()
    {
        InitializeComponent();
        _view = new ListCollectionView(Service.Items) { Filter = o => Matches((ShortcutItem)o) };
        List.ItemsSource = _view;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            Status.Text = "";
        };
    }

    private bool Matches(ShortcutItem item)
    {
        var q = Search.Text.Trim();
        return q.Length == 0 || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void OnTilePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
        {
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDraggingItem = false;
        }
    }

    private void OnTilePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDraggingItem) return;

        var currentPoint = e.GetPosition(this);
        var diff = _dragStartPoint - currentPoint;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShortcutItem item)
            {
                _isDraggingItem = true;
                item.IsDragging = true;
                try
                {
                    var dragData = new DataObject("ShortcutItem", item);
                    DragDrop.DoDragDrop(fe, dragData, DragDropEffects.Move);
                }
                finally
                {
                    item.IsDragging = false;
                    _isDraggingItem = false;
                    _lastDragEndTime = Environment.TickCount64;
                    ClearAllDropTargets();
                }
            }
        }
    }

    private void ClearAllDropTargets()
    {
        foreach (var it in Service.Items)
        {
            it.IsDropTarget = false;
            it.IsDragging = false;
        }
    }

    private void OnTileDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ShortcutItem"))
        {
            var sourceItem = e.Data.GetData("ShortcutItem") as ShortcutItem;
            if ((sender as FrameworkElement)?.DataContext is ShortcutItem targetItem && targetItem != sourceItem)
            {
                targetItem.IsDropTarget = true;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if ((sender as FrameworkElement)?.DataContext is ShortcutItem targetItem)
            {
                targetItem.IsDropTarget = true;
            }
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnTileDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShortcutItem targetItem)
        {
            targetItem.IsDropTarget = false;
        }
    }

    private void OnTileDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ShortcutItem"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnTileDrop(object sender, DragEventArgs e)
    {
        ClearAllDropTargets();
        _lastDragEndTime = Environment.TickCount64;

        if (e.Data.GetDataPresent("ShortcutItem"))
        {
            var sourceItem = e.Data.GetData("ShortcutItem") as ShortcutItem;
            var targetItem = (sender as FrameworkElement)?.DataContext as ShortcutItem;

            if (sourceItem != null && targetItem != null && sourceItem != targetItem)
            {
                var oldIdx = Service.Items.IndexOf(sourceItem);
                var newIdx = Service.Items.IndexOf(targetItem);
                if (oldIdx >= 0 && newIdx >= 0)
                {
                    Service.Items.Move(oldIdx, newIdx);
                    Service.Save();
                    _view.Refresh();
                    Flash($"Moved {sourceItem.Name}");
                }
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var targetItem = (sender as FrameworkElement)?.DataContext as ShortcutItem;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                var insertIdx = targetItem != null ? Service.Items.IndexOf(targetItem) : -1;
                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var newItem = new ShortcutItem { Name = name, Target = file };
                    if (insertIdx >= 0 && insertIdx < Service.Items.Count)
                    {
                        Service.Items.Insert(insertIdx++, newItem);
                    }
                    else
                    {
                        Service.Add(newItem);
                    }
                }
                Service.Save();
                _view.Refresh();
                Flash($"Added {files.Length} app(s)");
            }
            e.Handled = true;
        }
    }

    private void OnScrollDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ShortcutItem"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnScrollDrop(object sender, DragEventArgs e)
    {
        ClearAllDropTargets();
        _lastDragEndTime = Environment.TickCount64;

        if (e.Data.GetDataPresent("ShortcutItem"))
        {
            var sourceItem = e.Data.GetData("ShortcutItem") as ShortcutItem;
            if (sourceItem != null)
            {
                var oldIdx = Service.Items.IndexOf(sourceItem);
                var newIdx = Service.Items.Count - 1;
                if (oldIdx >= 0 && oldIdx != newIdx)
                {
                    Service.Items.Move(oldIdx, newIdx);
                    Service.Save();
                    _view.Refresh();
                    Flash($"Moved {sourceItem.Name} to end");
                }
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    Service.Add(new ShortcutItem { Name = name, Target = file });
                }
                _view.Refresh();
                Flash($"Added {files.Length} app(s)");
            }
            e.Handled = true;
        }
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (_isDraggingItem || Environment.TickCount64 - _lastDragEndTime < 400) return;
        if ((sender as FrameworkElement)?.DataContext is not ShortcutItem item) return;
        if (ShortcutsService.ActivateOrLaunch(item, forceNewInstance: false, out var error))
        {
            Flash($"Opened {item.Name}");
            NotchWindow.Instance?.Collapse();
        }
        else Flash(error ?? "Could not run", bad: true);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void OnRunNewInstance(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutItem item) return;
        if (ShortcutsService.ActivateOrLaunch(item, forceNewInstance: true, out var error))
        {
            Flash($"Started {item.Name}");
            NotchWindow.Instance?.Collapse();
        }
        else Flash(error ?? "Could not run", bad: true);
    }

    private void Flash(string text, bool bad = false)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, bad ? "BadBrush" : "GoodBrush");
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnNew(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShortcutItem item) OpenEditor(item);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is ShortcutItem item)
        {
            Service.Remove(item);
            _view.Refresh();
        }
    }

    private void OpenEditor(ShortcutItem? item)
    {
        _editing = item;
        NameInput.Text = item?.Name ?? "";
        TargetInput.Text = item is null ? "" : string.IsNullOrEmpty(item.Arguments) ? item.Target : $"\"{item.Target}\" {item.Arguments}";
        EditorPanel.Visibility = Visibility.Visible;
        NameInput.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => EditorPanel.Visibility = Visibility.Collapsed;

    /// <summary>Esc closes the editor first; only a second Esc leaves the tool.</summary>
    public bool OnEscape()
    {
        if (EditorPanel.Visibility != Visibility.Visible) return false;
        EditorPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnSave(sender, e);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an application", Filter = "Applications and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*" };
        bool? picked;
        using (NotchWindow.Instance?.HoldOpen())
            picked = NotchWindow.Instance is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (picked != true) return;
        TargetInput.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(NameInput.Text))
            NameInput.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        var (target, args) = SplitCommand(TargetInput.Text.Trim());
        if (name.Length == 0 || target.Length == 0)
        {
            Field.ShowError(name.Length == 0 ? NameInput : TargetInput);
            return;
        }

        if (_editing is null)
        {
            Service.Add(new ShortcutItem { Name = name, Target = target, Arguments = args });
        }
        else
        {
            _editing.Name = name;
            _editing.Target = target;
            _editing.Arguments = args;
            Service.Save();
            _view.Refresh();
        }
        EditorPanel.Visibility = Visibility.Collapsed;
    }

    private static (string Target, string Args) SplitCommand(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return (text[1..end], text[(end + 1)..].Trim());
        }
        if (File.Exists(text) || Directory.Exists(text) || text.Contains("://") || text.EndsWith(':'))
            return (text, "");
        var space = text.IndexOf(' ');
        return space > 0 ? (text[..space], text[(space + 1)..].Trim()) : (text, "");
    }
}
