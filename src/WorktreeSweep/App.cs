using System.Windows;

namespace WorktreeSweep;

/// <summary>The WPF application the window runs in, built in code with no <c>App.xaml</c>; it ends when the window closes.</summary>
internal sealed class App : Application
{
    /// <summary>Initializes a new instance of the <see cref="App"/> class, which shuts down when its main window closes.</summary>
    public App() => ShutdownMode = ShutdownMode.OnMainWindowClose;
}
