using Avalonia;
using Avalonia.Markup.Xaml;

namespace Bs1Cooler.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
