namespace HxPushApp
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        private async void OnViewMessagesClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//MessagesPage");
        }
    }
}
