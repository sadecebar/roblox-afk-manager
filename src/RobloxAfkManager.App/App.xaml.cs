using System.Windows;

namespace RobloxAfkManager.App;
public partial class App : Application
{
    // The app is where the Windows pieces are plugged into the engine.
    public App() => WindowsPlatform.Install();
}
