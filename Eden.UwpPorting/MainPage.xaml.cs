using Eden.UwpPorting.Core;
using Eden.UwpPorting.Gpu;
using Eden.UwpPorting.Vfs;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Eden.UwpPorting;

public sealed partial class MainPage : Page
{
    private readonly UwpStorageProvider _storageProvider = new();
    private CancellationTokenSource? _bootCts;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnLoadKeysClicked(object sender, RoutedEventArgs e)
    {
        await _storageProvider.InitializeAsync();
        await _storageProvider.PickAndImportKeysAsync();
        StatusText.Text = "Keys importadas para LocalFolder/keys.";
    }

    private async void OnLoadGameClicked(object sender, RoutedEventArgs e)
    {
        await _storageProvider.InitializeAsync();

        StorageFile? file = await _storageProvider.PickGameFileAsync();
        if (file is null)
        {
            StatusText.Text = "Seleção de jogo cancelada.";
            return;
        }

        StatusText.Text = $"Jogo selecionado: {file.Name}. Iniciando runtime...";

        _bootCts?.Cancel();
        _bootCts = new CancellationTokenSource();

        var bootstrap = new PortingBootstrap();
        var settings = new EdenSettings(file.Path);
        EdenRuntime runtime = bootstrap.Build(settings, RenderPanel);

        _ = Task.Run(async () =>
        {
            try
            {
                await runtime.BootAsync(_bootCts.Token);
            }
            catch (OperationCanceledException)
            {
                // normal
            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    StatusText.Text = $"Erro no boot: {ex.Message}";
                });
            }
        });
    }
}
