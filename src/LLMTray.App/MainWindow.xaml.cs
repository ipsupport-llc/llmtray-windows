using LLMTray.Core;
using Microsoft.UI.Xaml;

namespace LLMTray.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ApiLine.Text = AppInfo.ApiBaseUrl;
    }
}
