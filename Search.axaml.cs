using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace HoyoversePckPlayer;

public partial class Search : Window
{
    public MainWindow? MWindow;
    List<Wwise> _found = [];
    
    public Search()
    {
        InitializeComponent();
    }

    async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Design.IsDesignMode)
            return;
        ListBox.Items.Clear();
        _found.Clear();
        var prog = new Progress()
        {
            Text =
            {
                Text = "Searching...",
            },
            ProgressBar =
            {
                ShowProgressText = true,
            },
        };
        prog.Show(this);
        var sel = ComboBox.SelectedIndex;
        var search = TextBox.Text!;
        var ignoreCasing = !CheckBox.IsChecked!.Value;
        await Task.Run(() =>
        {
            var tot = MWindow!.Pcks.Sum(p => p.FileSystem.Count);
            Dispatcher.UIThread.Invoke(() => prog.ProgressBar.Maximum = tot);
            if (sel == 2)
            {
                Dispatcher.UIThread.Invoke(() => prog.Text.Text = "Updating search cache...");
                var sw = Stopwatch.StartNew();
                var done = 0;
                foreach (var p in MWindow.Pcks)
                {
                    foreach (var v in p.FileSystem.Values)
                    {
                        v.SongHash ??= Blake3.Hasher.Hash(v.GetRaw()).ToString();
                        done++;
                        if (sw.ElapsedMilliseconds >= 300)
                        {
                            Dispatcher.UIThread.Invoke(() => prog.ProgressBar.Value = done);
                            sw.Restart();
                        }
                    }
                }
            }
            Dispatcher.UIThread.Invoke(() =>
            {
                prog.Text.Text = "Searching...";
                prog.ProgressBar.Value = 0;
            });
            {
                var sw = Stopwatch.StartNew();
                var done = 0;
                foreach (var p in MWindow.Pcks)
                {
                    foreach (var v in p.FileSystem.Values)
                    {
                        var match = false;
                        switch (sel)
                        {
                            case 0:
                                if (($"{v.PckName}/{v.Path}").Contains(search,
                                        ignoreCasing ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture))
                                    match = true;
                                break;
                            case 1:
                                if (v.Path != null && v.Path.Contains(search,
                                        ignoreCasing ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture))
                                    match = true;
                                break;
                            case 2:
                                if (v.SongHash != null && v.SongHash.Contains(search,
                                        ignoreCasing ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture))
                                    match = true;
                                break;
                        }
                        if (match)
                        {
                            _found.Add(v);
                            Dispatcher.UIThread.Invoke(() => ListBox.Items.Add(v.Name!));
                        }
                        done++;
                        if (sw.ElapsedMilliseconds >= 300)
                        {
                            Dispatcher.UIThread.Invoke(() => prog.ProgressBar.Value = done);
                            sw.Restart();
                        }
                    }
                }
            }
        });
        prog.Close();
    }

    void ListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ListBox.SelectedIndex >= 0 && ListBox.SelectedIndex < _found.Count)
        {
            var item = _found[ListBox.SelectedIndex].Item!;
            MWindow!.TreeExpandAll((TreeViewItem)item.Parent!);
            MWindow.SongTree.SelectedItem = item;
        }
    }
}