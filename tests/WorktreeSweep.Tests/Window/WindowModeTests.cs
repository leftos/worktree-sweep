using System.Reflection;
using System.Windows;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.Window;

/// <summary>The default mode's start: the thread it needs, a root it cannot use, and the first screen with and without a root.</summary>
public sealed class WindowModeTests
{
    /// <summary>A root that is not a path fails with exit 1 before any WPF application, so no window, is created.</summary>
    [Fact]
    public void BadRootFailsBeforeAnyWindow()
    {
        int code = WindowMode.Run("bad\0root");

        Assert.Equal(1, code);
        Assert.Null(Application.Current);
    }

    /// <summary>With no root, the window opens on the Choose screen with the current directory in the folder box, and no scan begins.</summary>
    [Fact]
    public void NoRootOpensChoose()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);

        WindowMode.Open(main, null, @"C:\started\here");

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.Equal(@"C:\started\here", main.Folder);
        Assert.Null(main.Scanning);
    }

    /// <summary>With a root, the window puts it in the folder box and starts scanning it at once.</summary>
    [Fact]
    public void RootStartsTheScan()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        string root = Path.GetFullPath(Path.GetTempPath());

        WindowMode.Open(main, root, @"C:\started\here");

        Assert.Equal(Screen.Scanning, main.Screen);
        Assert.Equal(root, main.Folder);
        Assert.Equal(root, main.Scanning?.Root);
    }

    /// <summary>The program's entry point runs on an STA thread, which the window needs.</summary>
    [Fact]
    public void MainIsStaThread()
    {
        Type program =
            typeof(WindowMode).Assembly.GetType("WorktreeSweep.Program") ?? throw new InvalidOperationException("no WorktreeSweep.Program");
        MethodInfo main =
            program.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("no Program.Main");

        Assert.NotNull(main.GetCustomAttribute<STAThreadAttribute>());
    }
}
