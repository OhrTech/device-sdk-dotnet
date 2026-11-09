namespace OhrTech.Device.Maui.Sample;

public sealed class App(MainPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(page);
}
