using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HxPushApp
{
    public partial class MessagesPage : ContentPage, INotifyPropertyChanged
    {
        public ObservableCollection<MessageItem> Messages { get; } = new();

        string summaryText = "正在加载消息...";
        public string SummaryText
        {
            get => summaryText;
            set
            {
                if (summaryText == value)
                {
                    return;
                }

                summaryText = value;
                OnPropertyChanged();
            }
        }

        public MessagesPage()
        {
            InitializeComponent();
            BindingContext = this;
            Loaded += OnLoaded;
        }

        async void OnLoaded(object? sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            await LoadMessagesAsync();
        }

        async Task LoadMessagesAsync()
        {
            try
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync("msgList.json");
                var response = await JsonSerializer.DeserializeAsync<MessageListResponse>(stream);

                //using HttpClient client = new HttpClient();
                //var ipRet = await client.GetAsync("https://ip.huixingfifa.top");
                //Title = await ipRet.Content.ReadAsStringAsync();

                Messages.Clear();
                foreach (var message in response?.Data ?? [])
                {
                    Messages.Add(message);
                }

                SummaryText = $"共 {Messages.Count} 条消息";
            }
            catch
            {
                Messages.Clear();
                SummaryText = "消息加载失败";
            }
        }

        async void OnMessageSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not MessageItem message)
            {
                return;
            }

            if (sender is CollectionView collectionView)
            {
                collectionView.SelectedItem = null;
            }

            await Shell.Current.GoToAsync(nameof(MessageDetailPage), new Dictionary<string, object>
            {
                ["Message"] = message
            });
        }

        public new event PropertyChangedEventHandler? PropertyChanged;

        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class MessageListResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [JsonPropertyName("data")]
        public List<MessageItem>? Data { get; set; }
    }

    public sealed class MessageItem
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("hwid")]
        public string Hwid { get; set; } = string.Empty;
    }
}
