using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Eden.UwpPorting;

sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Frame rootFrame = Window.Current.Content as Frame ?? new Frame();

        if (rootFrame.Content == null)
        {
            rootFrame.Navigate(typeof(MainPage), e.Arguments);
        }

        Window.Current.Content = rootFrame;
        Window.Current.Activate();
    }
}
