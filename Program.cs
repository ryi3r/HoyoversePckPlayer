using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HoyoversePckPlayer;

abstract class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /*public static void Main()
    {
        //"-I", "-o", "<outputFile>", "-i", "<inputFile>"
        var vgm = new VgmStreamWasm(false);
        Console.WriteLine("Init ok!");
        var pck = new Pck("/run/media/_/Files/Games/TheHonkersRailwayLauncher/HSR/StarRail_Data/StreamingAssets/Audio/AudioPackage/Windows/Music2.pck");
        pck.Read();
        Console.WriteLine($"Read pck, contains {pck.FileSystem.Count} entries");
        var chose = "";
        {
            var last = 0L;
            foreach (var (i, e) in pck.FileSystem)
            {
                if (e.Size > last)
                {
                    chose = i;
                    last = e.Size;
                }
            }
        }
        if (chose.Length == 0)
            throw new("invalid element chosen");
        var w = pck.FileSystem[chose];
        for (var i = 0; i < 100000; i++)
            w.GetWav(vgm).Dispose();
        {
            var t = new Stopwatch();
            t.Start();
            //var r = vgms.CallMain("-h");
            var r = vgm.CallMain("-o", "/.wav", "-i", "/.wem");
            Console.WriteLine($"Out: {r}");
            r = vgm.CallMain("-o", "/1.wav", "-i", "/.wem");
            Console.WriteLine($"Out: {r}");
            t.Stop();
            vgm.FlushLogToConsole();
            Console.WriteLine($"Took {t.Elapsed.TotalMilliseconds}ms");
        }
        {
            var r = vgm.Fs.FetchResource("/.wav")!.ToArray();
            var f = new BinaryWriter(File.Create("output.wav"));
            f.Write(r);
            f.Flush();
            f.Dispose();
        }
        vgm.Dispose();
    }*/

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
