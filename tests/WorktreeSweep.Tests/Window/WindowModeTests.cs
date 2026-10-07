using System.Reflection;
using System.Windows;

namespace WorktreeSweep.Tests.Window;

/// <summary>The default mode's start: the thread it needs and a root it cannot use.</summary>
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
