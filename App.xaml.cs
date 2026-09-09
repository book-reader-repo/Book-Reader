using Microsoft.Maui.Controls;

namespace BookViewer;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new MainPage();
    }
}
