public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new NavigationPage(new Views.LibraryPage())
        {
            BarBackgroundColor = Colors.Transparent,
            BarTextColor = Colors.White
        };
    }
}
