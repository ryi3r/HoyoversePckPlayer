using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ManagedBass;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HoyoversePckPlayer;

public partial class MainWindow : Window
{
    public enum LoopMode
    {
        None,
        Next,
        Previous,
        Shuffle,
        LoopOne,
        LoopPack,
        LoopAll,
    }
    public List<Pck> Pcks = [];
    //public Dictionary<TreeViewItem, int> PckTable = [];
    public Dictionary<TreeViewItem, (int, string)> EntryTable = [];

    public Wwise? SelectedItem;
    public Wwise? PlayingItem;
    public TreeViewItem? PlayingTree;
    public DispatcherTimer Timer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100),
    };
    public bool IsAudioPlaying;
    public VgmStreamWasm Vgm = new(true);
    public Lock Lock = new();
    public bool IsInTimer;
    public LoopMode Loop = LoopMode.None;
    public bool LoopDone;
    public bool ErroredOut;
    public int? ChannelHandle;
    // last item is always the current item
    public List<TreeViewItem> Playlist = [];
    public int PlaylistCurrent;
    public bool SelectionUpdatePlaylist = true;
    
    public MainWindow()
    {
        InitializeComponent();
        Bass.Init();
        
        Timer.Tick += (_, _) =>
        {
            IsInTimer = true;
            try
            {
                if (ErroredOut || (ChannelHandle != null && Bass.ChannelIsActive((int)ChannelHandle) == PlaybackState.Stopped && IsAudioPlaying && !LoopDone))
                {
                    ErroredOut = false;
                    LoopDone = true;
                    Next_OnClick(null, null);
                }
                PlayButton.Content = IsAudioPlaying && SelectedItem == PlayingItem && PlayingItem != null ? "Stop" : "Play";
                PauseButton.Content = ChannelHandle != null && Bass.ChannelIsActive((int)ChannelHandle) == PlaybackState.Paused ? "Resume" : "Pause";
                LoopButton.Content = (Loop) switch
                {
                    LoopMode.None => "Loop: None",
                    LoopMode.Next => "Loop: Next",
                    LoopMode.Previous => "Loop: Last",
                    LoopMode.Shuffle => "Loop: Shuffle",
                    LoopMode.LoopOne => "Loop: Loop One",
                    LoopMode.LoopPack => "Loop: Loop Pack",
                    LoopMode.LoopAll => "Loop: Loop All",
                    _ => "Loop: ???",
                };
                
                if (SelectedItem != PlayingItem /*|| !MediaPlayer.IsPlaying*/)
                {
                    SongCurrentDuration.Text = "00:00";
                    SongSlider.Value = 0;
                    if (SelectedItem != null)
                        SongTotalDuration.Text = TimeMsToString((long)(SelectedItem.Duration * 1000d));
                    if (PlayingItem != null)
                    {
                        if (!GoToPlayingSongButton.IsVisible)
                            GoToPlayingSongButton.IsVisible = true;
                    }
                    else if (GoToPlayingSongButton.IsVisible)
                        GoToPlayingSongButton.IsVisible = false;
                    return;
                }
                else if (GoToPlayingSongButton.IsVisible)
                    GoToPlayingSongButton.IsVisible = false;
                if (ChannelHandle == null)
                    return;
                var tot = Bass.ChannelBytes2Seconds((int)ChannelHandle, Bass.ChannelGetLength((int)ChannelHandle));
                var pos = Bass.ChannelBytes2Seconds((int)ChannelHandle, Bass.ChannelGetPosition((int)ChannelHandle));
                SongCurrentDuration.Text = TimeMsToString((long)(pos * 1000));
                SongTotalDuration.Text = TimeMsToString((long)(tot * 1000));
                //Console.WriteLine(pos / tot);
                SongSlider.Value = pos / tot;
            }
            finally
            {
                IsInTimer = false;
            }
            /*if (ChannelHandle != null)
                Bass.ChannelSetPosition((int)ChannelHandle, Bass.ChannelGetLength((int)ChannelHandle) - 1);*/
        };
        Timer.Start();
        /*var vgm = new VgmStreamWasm(false);
        for (var i = 0; i < 1000000; i++)
        {
            Console.WriteLine(i);
            vgm.CallMain("-h");
        }
        vgm.Dispose();*/
    }

    void TreeExpandAll(TreeViewItem tvi)
    {
        while (true)
        {
            if (tvi.Items.Count > 0)
                SongTree.ExpandSubTree(tvi);
            if (tvi.Parent == null)
                break;
            if (tvi.Parent is TreeViewItem nTvi)
                tvi = nTvi;
            else
                break;
        }
    }
    
    bool IsHeaderValid(string header)
    {
        header = header.ToLower();
        if (!(PlayBanks.IsChecked ?? true) && header.Contains("banks"))
            return false;
        if (!(PlayExternal.IsChecked ?? true) && header.Contains("external"))
            return false;
        if (!(PlayStreamed.IsChecked ?? true) && header.Contains("streamed"))
            return false;
        if (!(PlayMusic.IsChecked ?? true) && header.Contains("music"))
            return false;
        return true;
    }

    public bool PlayNext(bool loop)
    {
        if (PlayingTree == null)
            return false;
        if (PlayingTree!.Parent! is not TreeViewItem tvi)
            return true;
        var index = tvi.Items.IndexOf(PlayingTree);
        if (index + 1 < tvi.Items.Count && IsHeaderValid((string)tvi.Header!))
            SongTree.SelectedItem = tvi.Items[index + 1];
        else
        {
            var add = 1;
            while (true)
            {
                var n = (ItemCollection)((dynamic)tvi.Parent!).Items;
                var i = n.IndexOf(tvi);
                if (i >= 0 && i + add < n.Count)
                {
                    tvi = (TreeViewItem)((dynamic)tvi.Parent!).Items[i + add]!;
                    if (!IsHeaderValid((string)tvi.Header!))
                    {
                        add++;
                        continue;
                    }
                    if (tvi.Items.Count > 0)
                    {
                        TreeExpandAll(tvi);
                        SongTree.SelectedItem = tvi.Items[0];
                    }
                    else
                        throw new("unable to find next item!");
                    break;
                }
                if (tvi.Parent.Parent is TreeViewItem nTvi)
                {
                    add = 1;
                    i = nTvi.Items.IndexOf(tvi.Parent);
                    if (i >= 0 && i + 1 < tvi.Items.Count && nTvi.Items[i + 1] is TreeViewItem mTvi)
                        tvi = mTvi;
                    else
                        throw new("unable to find next item!");
                }
                else if (loop)
                {
                    tvi = (TreeViewItem)SongTree.Items[0]!;
                    add = 0;
                    if (!(PlayBanks.IsChecked ?? true) && !(PlayExternal.IsChecked ?? true) && !(PlayStreamed.IsChecked ?? true) && !(PlayMusic.IsChecked ?? true))
                        return false;
                }
                else
                    return false;
            }
        }
        return true;
    }

    public bool PlayPrevious(bool loop)
    {
        if (PlayingTree == null)
            return false;
        if (PlayingTree!.Parent! is not TreeViewItem tvi)
            return true;
        var index = tvi.Items.IndexOf(PlayingTree);
        if (index - 1 < tvi.Items.Count && IsHeaderValid((string)tvi.Header!))
            SongTree.SelectedItem = tvi.Items[index - 1];
        else
        {
            var rem = 1;
            while (true)
            {
                var n = (ItemCollection)((dynamic)tvi.Parent!).Items;
                var i = n.IndexOf(tvi);
                if (i - rem >= 0 && i - rem < n.Count)
                {
                    tvi = (TreeViewItem)((dynamic)tvi.Parent!).Items[i - rem]!;
                    if (!IsHeaderValid((string)tvi.Header!))
                    {
                        rem++;
                        continue;
                    }
                    if (tvi.Items.Count > 0)
                    {
                        TreeExpandAll(tvi);
                        SongTree.SelectedItem = tvi.Items[0];
                    }
                    else
                        throw new("unable to find next item!");
                    break;
                }
                if (tvi.Parent.Parent is TreeViewItem nTvi)
                {
                    rem = 1;
                    i = nTvi.Items.IndexOf(tvi.Parent);
                    if (i > 0 && i - 1 < tvi.Items.Count && nTvi.Items[i - 1] is TreeViewItem mTvi)
                        tvi = mTvi;
                    else
                        throw new("unable to find next item!");
                }
                else if (loop)
                {
                    tvi = (TreeViewItem)SongTree.Items[^1]!;
                    rem = 0;
                    if (!(PlayBanks.IsChecked ?? true) && !(PlayExternal.IsChecked ?? true) && !(PlayStreamed.IsChecked ?? true) && !(PlayMusic.IsChecked ?? true))
                        return false;
                }
                else
                    return false;
            }
        }
        return true;
    }
    
    public static string TimeMsToString(long time)
    {
        var sec = (int)Math.Floor(time / 1000d) % 60;
        var min = (int)Math.Floor(time / (1000d * 60d)) % 60;
        var hour = (int)Math.Floor(time / (1000d * 60d * 60d)) % 24;
        var day = (int)Math.Floor(time / (1000d * 60d * 60d * 24d));

        if (day > 0)
            return $"{day:00}:{hour:00}:{min:00}:{sec:00}";
        return hour > 0 ? $"{hour:00}:{min:00}:{sec:00}" : $"{min:00}:{sec:00}";
    }

    static readonly Regex CompareRegex = new("([0-9]+)", RegexOptions.Compiled);
    
    public static int CompareString(string x, string y)
    {
        if (x == y)
            return 0;
        
        var x1 = CompareRegex.Split(x.Replace(" ", ""));
        var y1 = CompareRegex.Split(y.Replace(" ", ""));

        for (var i = 0; i < x1.Length && i < y1.Length; i++)
        {
            if (x1[i] == y1[i])
                continue;
            if (!int.TryParse(x1[i], out var x2) || !int.TryParse(y1[i], out var y2))
                return string.Compare(x1[i], y1[i], StringComparison.Ordinal);
            return x2.CompareTo(y2);
        }
        if (y1.Length > x1.Length)
            return 1;
        if (x1.Length > y1.Length)
            return -1;
        return 0;
    }
    
    async void LoadFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new());
        var folders = folder.Select(entry => ((string, string, TreeViewItem?))(entry.Path.AbsolutePath, entry.Path.AbsolutePath, null)).ToList();
        while (folders.Count > 0)
        {
            var (b, p, pN) = folders[0];
            var n = b == p ? null : new TreeViewItem()
            {
                Header = p.StartsWith(b) ? p[(b.Length + (b.Length > 0 && b[^1] == '/' ? 0 : 1))..] : p,
            };
            folders.RemoveAt(0);
            folders.AddRange(Directory.EnumerateDirectories(p).Select(f => (p.Replace("\\", "/"), f, n)));
            if (n != null)
            {
                if (pN == null)
                {
                    var index = SongTree.Items.Count;
                    for (var i = 0; i < SongTree.Items.Count; i++)
                    {
                        index = i + 1;
                        var cTvi = (TreeViewItem)SongTree.Items[i]!;
                        if (CompareString((string)cTvi.Header!, (string)n.Header!) < 0)
                            continue;
                        index = i;
                        break;
                    }
                    SongTree.Items.Insert(index, n);
                }
                else
                {
                    var index = pN.Items.Count;
                    for (var i = 0; i < pN.Items.Count; i++)
                    {
                        index = i + 1;
                        var cTvi = (TreeViewItem)pN.Items[i]!;
                        if (CompareString((string)cTvi.Header!, (string)n.Header!) < 0)
                            continue;
                        index = i;
                        break;
                    }
                    pN.Items.Insert(index, n);
                }
            }
            foreach (var f in Directory.EnumerateFiles(p))
            {
                if (!f.EndsWith(".pck") && !f.EndsWith(".bnk"))
                    continue;
                var pck = new Pck(f);
                var pckIndex = Pcks.Count;
                Pcks.Add(pck);
                var h = f[(f.Replace("\\", "/").LastIndexOf('/') + 1)..];
                var tvi = new TreeViewItem()
                {
                    Header = h,
                };
                //PckTable.Add(tvi, pckIndex);
                pck.Read(h);
                if (n == null)
                {
                    var index = SongTree.Items.Count;
                    for (var i = 0; i < SongTree.Items.Count; i++)
                    {
                        index = i + 1;
                        var cTvi = (TreeViewItem)SongTree.Items[i]!;
                        if (CompareString((string)cTvi.Header!, (string)tvi.Header!) < 0)
                            continue;
                        index = i;
                        break;
                    }
                    SongTree.Items.Insert(index, tvi);
                }
                else
                {
                    var index = 0;
                    for (var i = 0; i < n.Items.Count; i++)
                    {
                        index = i + 1;
                        var cTvi = (TreeViewItem)n.Items[i]!;
                        if (CompareString((string)cTvi.Header!, (string)tvi.Header!) < 0)
                            continue;
                        index = i;
                        break;
                    }
                    n.Items.Insert(index, tvi);
                }
                foreach (var j in pck.FileSystem.Keys.Order())
                {
                    var nTvi = new TreeViewItem()
                    {
                        Header = pck.FileSystem[j].Path ?? "<unknown>",
                    };
                    tvi.Items.Add(nTvi);
                    EntryTable.Add(nTvi, (pckIndex, j));
                }
            }
        }
    }

    void AddToPlaylist(TreeViewItem tvi)
    {
        if (PlaylistCurrent >= 0 && PlaylistCurrent < Playlist.Count)
            Playlist[^1] = tvi;
        else
            Playlist.Add(tvi);
    }
    
    void SongTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SongTree.SelectedItem == null)
            return;
        var tvi = (TreeViewItem)SongTree.SelectedItem!;
        if (!EntryTable.TryGetValue(tvi, out var v))
            return;
        SelectedItem = Pcks[v.Item1].FileSystem[v.Item2];
        if (SelectionUpdatePlaylist)
            AddToPlaylist(tvi);
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (SelectedItem.Duration == -1)
        {
            var targetFilename = $"/{Path.GetRandomFileName()}.{SelectedItem.Extension.ToString().ToLower()}";
            string json;
            lock (Lock)
            {
                Vgm.Fs.InsertResource(targetFilename, SelectedItem.GetRaw());
                var lastIgnore = Vgm.Fs.IgnoreInternalPipes; 
                Vgm.Fs.IgnoreInternalPipes = false;
                Vgm.CallMain("-I", "-m", targetFilename);
                Vgm.Fs.IgnoreInternalPipes = lastIgnore;
                Vgm.Fs.RemoveResource(targetFilename);
                json = Encoding.UTF8.GetString(Vgm.FlushStdout());
                Vgm.FlushStderr();
            }
            try
            {
                var metadata = JsonSerializer.Deserialize<JsonElement>(json, JsonSerializerOptions.Default);
                SelectedItem.Duration = (double)metadata.GetProperty("numberOfSamples").GetInt32() / metadata.GetProperty("sampleRate").GetInt32();
            }
            catch
            {
                SelectedItem.Duration = 0;
            }
            //Console.WriteLine($"finished: {SelectedItem.Name}");
        }
        SongName.Text = $"{SelectedItem.PckName}/{SelectedItem.Path}";
        SongTotalDuration.Text = $"{SelectedItem.Duration / 60:00}:{SelectedItem.Duration % 60:00}";
    }

    void PlayButton_OnClick(object? sender, RoutedEventArgs? e)
    {
        if (ChannelHandle != null)
        {
            Bass.ChannelStop((int)ChannelHandle);
            Bass.StreamFree((int)ChannelHandle);
        }
        if (IsAudioPlaying && SelectedItem == PlayingItem && PlayingItem != null && e != null)
        {
            PlayingItem = null;
            IsAudioPlaying = false;
        }
        else
        {
            if (PlaylistCurrent >= 0 && PlaylistCurrent < Playlist.Count)
            {
                var tvi = Playlist[PlaylistCurrent++];
                if (!EntryTable.TryGetValue(tvi, out var v))
                    return;
                SelectedItem = Pcks[v.Item1].FileSystem[v.Item2];
                PlayingItem = SelectedItem;
                PlayingTree = tvi;
                TreeExpandAll(tvi);
                SelectionUpdatePlaylist = false;
                SongTree.SelectedItem = tvi;
                SelectionUpdatePlaylist = true;
            }
            else if (SelectedItem != null)
            {
                PlayingItem = SelectedItem;
                PlayingTree = (TreeViewItem)SongTree.SelectedItem!;
            }
            while (Playlist.Count > 128)
            {
                Playlist.RemoveAt(0);
                if (PlaylistCurrent > 0)
                    PlaylistCurrent--;
            }
            /*Console.WriteLine(string.Join('\n', Playlist.Select(x =>
            {
                var e = EntryTable[x];
                var w = Pcks[e.Item1].FileSystem[e.Item2];
                return $"{w.PckName}/{w.Path}";
            })));
            Console.WriteLine(Playlist.Count);*/
            MemoryStream audioStream;
            lock (Lock)
                audioStream = PlayingItem!.GetWav(Vgm);
            ChannelHandle = Bass.CreateStream(audioStream.GetBuffer(), 0, audioStream.Length, BassFlags.Default);
            if (ChannelHandle == 0)
            {
                Console.WriteLine($"Got an error while trying to create an audio stream: {Bass.LastError}");
                ChannelHandle = null;
                ErroredOut = true;
            }
            else
            {
                Bass.ChannelPlay((int)ChannelHandle);
                LoopDone = false;
                IsAudioPlaying = true;
            }
        }
    }

    void SongSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (IsInTimer || SelectedItem != PlayingItem || PlayingItem == null || ChannelHandle == null)
            return;
        // length - 1 because it loops around at length
        Bass.ChannelSetPosition((int)ChannelHandle, (long)((Bass.ChannelGetLength((int)ChannelHandle) - 1) * SongSlider.Value));
    }

    void GoToPlayingSongButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (PlayingTree == null)
            return;
        TreeExpandAll((TreeViewItem)PlayingTree.Parent!);
        SongTree.SelectedItem = PlayingTree;
    }

    void Loop_OnClick(object? sender, RoutedEventArgs e)
    {
        Loop++;
        if (Loop > LoopMode.LoopAll)
            Loop = LoopMode.None;
    }

    void Last_OnClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistCurrent <= 1)
            return;
        PlaylistCurrent -= 2;
        PlayButton_OnClick(null, null);
    }

    void Next_OnClick(object? sender, RoutedEventArgs? _)
    {
        var isOk = true;
        if (Playlist.Count <= PlaylistCurrent)
        {
            switch (Loop)
            {
                case LoopMode.None:
                    isOk = false;
                    break;
                case LoopMode.Next:
                    isOk = PlayNext(false);
                    break;
                case LoopMode.Previous:
                    isOk = PlayPrevious(false);
                    break;
                case LoopMode.Shuffle:
                    {
                        var rng = new Random();
                        int a;
                        // todo: naive solution, ideally we want to collect a list of usable songs 
                        for (a = 0; a < 10000; a++)
                        {
                            var i = rng.Next(0, EntryTable.Count);
                            var e = EntryTable.ElementAt(i).Key;
                            if (e.Parent is not TreeViewItem tvi)
                                continue;
                            if (!IsHeaderValid((string)tvi.Header!))
                                continue;
                            TreeExpandAll(tvi);
                            SongTree.SelectedItem = e;
                            break;
                        }
                        if (a >= 10000)
                            isOk = false;
                    }
                    break;
                case LoopMode.LoopOne:
                    isOk = false;
                    if (ChannelHandle != null)
                        Bass.ChannelSetPosition((int)ChannelHandle, 0);
                    break;
                case LoopMode.LoopPack:
                    {
                        if (PlayingTree!.Parent! is TreeViewItem tvi)
                        {
                            var index = tvi.Items.IndexOf(PlayingTree);
                            if (index + 1 < tvi.Items.Count && IsHeaderValid((string)tvi.Header!))
                                SongTree.SelectedItem = tvi.Items[index + 1];
                            else
                                SongTree.SelectedItem = tvi.Items[0];
                        }
                        else
                            isOk = false;
                    }
                    break;
                case LoopMode.LoopAll:
                    isOk = PlayNext(true);
                    break;
            }
            if (!isOk)
                return;
            //AddToPlaylist((TreeViewItem)SongTree.SelectedItem!);
        }
        PlayButton_OnClick(null, null);
    }

    async void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedItem == null)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            DefaultExtension = ".wav",
            FileTypeChoices = [new(SelectedItem.IsWem ? "*.wem" : "*.bnk"), new("*.wav"), new("*.ogg")],
        });
        if (file == null)
            return;
        var f = await file.OpenWriteAsync();
        var ext = Path.GetExtension(file.Path.AbsoluteUri);

        MemoryStream stream;
        lock (Lock)
        {
            stream = ext switch
            {
                ".wav" => SelectedItem.GetWav(Vgm),
                ".ogg" => SelectedItem.GetOgg(Vgm),
                _ => new(SelectedItem.GetRaw()),
            };
        }
        await stream.CopyToAsync(f);
        f.Close();
        await f.DisposeAsync();
        await stream.DisposeAsync();
    }

    async void ExportAll_OnClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            DefaultExtension = ".wav",
            FileTypeChoices = [new("*.raw"), new("*.wav"), new("*.ogg")],
        });
        if (file == null)
            return;
        Directory.CreateDirectory(file.Path.AbsolutePath);
        var bPath = $"{file.Path.AbsolutePath}/";
        var ext = Path.GetExtension(file.Path.AbsoluteUri);
        var vgms = new List<(VgmStreamWasm, Lock)>();
        for (var i = 0; i < Environment.ProcessorCount; i++)
            vgms.Add((new(true), new()));
        var n = 0;
        var tasks = new List<Task>();
        foreach (var (i, pck) in Pcks.Index())
        {
            var dPath = $"{bPath}/{pck.Name}_{i}";
            Directory.CreateDirectory(dPath);
            foreach (var w in pck.FileSystem.Values)
            {
                while (tasks.Count >= vgms.Count)
                {
                    while (true)
                    {
                        var gotTask = tasks.FirstOrDefault(t => t.IsCompleted);
                        /*foreach (var t in tasks)
                            Console.WriteLine(t.Status);*/
                        if (gotTask == null)
                        {
                            await Task.Delay(5);
                            continue;
                        }
                        tasks.Remove(gotTask);
                        break;
                    }
                }
                var cur = vgms[n];
                n++;
                n %= vgms.Count;
                tasks.Add(Task.Run(async () =>
                {
                    MemoryStream stream;
                    lock (cur.Item2)
                    {
                        stream = ext switch
                        {
                            ".wav" => w.GetWav(cur.Item1),
                            ".ogg" => w.GetOgg(cur.Item1),
                            _ => new(w.GetRaw()),
                        };
                    }
                    stream.Position = 0;
                    var fPath = $"{dPath}/{w.Name ?? "unknown"}";
                    if (stream.Length > 0)
                    {
                        switch (ext)
                        {
                            case ".wav":
                                fPath += ".wav";
                                break;
                            case ".ogg":
                                fPath += ".ogg";
                                break;
                        }
                    }
                    else
                    {
                        await stream.DisposeAsync();
                        stream = new(w.GetRaw());
                    }
                    var f = File.Create(fPath);
                    await stream.CopyToAsync(f);
                    await stream.DisposeAsync();
                    f.Close();
                    await f.DisposeAsync();
                }));
            }
        }
        foreach (var t in tasks)
            await t;
    }

    void ClearData_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ChannelHandle != null)
        {
            Bass.ChannelStop((int)ChannelHandle);
            Bass.StreamFree((int)ChannelHandle);
            ChannelHandle = null;
        }
        SongTree.Items.Clear();
        Pcks.Clear();
        //PckTable.Clear();
        EntryTable.Clear();
        Playlist.Clear();
        SelectedItem = null;
        PlayingTree = null;
        PlayingItem = null;
        IsAudioPlaying = false;
        PlaylistCurrent = 0;
    }

    async void LoadFile_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            AllowMultiple = true,
            FileTypeFilter = [new("*.pck")],
        });
        foreach (var file in files)
        {
            var pck = new Pck(file.Path.AbsolutePath);
            var pckIndex = Pcks.Count;
            Pcks.Add(pck);
            var h = file.Path.AbsolutePath[(file.Path.AbsolutePath.Replace("\\", "/").LastIndexOf('/') + 1)..];
            var tvi = new TreeViewItem()
            {
                Header = h,
            };
            //PckTable.Add(tvi, pckIndex);
            pck.Read(h);
            var index = SongTree.Items.Count;
            for (var i = 0; i < SongTree.Items.Count; i++)
            {
                index = i + 1;
                var cTvi = (TreeViewItem)SongTree.Items[i]!;
                if (CompareString((string)cTvi.Header!, (string)tvi.Header) < 0)
                    continue;
                index = i;
                break;
            }
            SongTree.Items.Insert(index, tvi);
            foreach (var (j, d) in pck.FileSystem)
            {
                var nTvi = new TreeViewItem()
                {
                    Header = d.Path ?? "<unknown>",
                };
                tvi.Items.Add(nTvi);
                EntryTable.Add(nTvi, (pckIndex, j));
            }
        }
    }

    void PauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ChannelHandle == null)
            return;
        if (Bass.ChannelIsActive((int)ChannelHandle) == PlaybackState.Paused)
            Bass.ChannelPlay((int)ChannelHandle);
        else
            Bass.ChannelPause((int)ChannelHandle);
    }
}
