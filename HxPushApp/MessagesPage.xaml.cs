using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using HxPushApp.Helpers.Sqlite;
using HxPushApp.models.Message;

namespace HxPushApp
{
    public partial class MessagesPage : ContentPage, INotifyPropertyChanged
    {
        const int PageSize = 50;

        readonly SemaphoreSlim loadLock = new(1, 1);
        bool hasMoreMessages = true;
        bool isLoadingMore;
        bool isToastVisible;
        bool hasShownNoMoreToast;

        public ObservableCollection<HxPushMsgModel> Messages { get; } = new();

        public bool IsLoadingMore
        {
            get => isLoadingMore;
            private set
            {
                if (isLoadingMore == value)
                {
                    return;
                }

                isLoadingMore = value;
                OnPropertyChanged();
            }
        }

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
            await RefreshMessagesAsync();
        }

        async void OnRefreshing(object? sender, EventArgs e)
        {
            try
            {
                await RefreshMessagesAsync();
            }
            finally
            {
                if (sender is RefreshView refreshView)
                {
                    refreshView.IsRefreshing = false;
                }
            }
        }

        async Task RefreshMessagesAsync()
        {
            await loadLock.WaitAsync();
            try
            {
                Messages.Clear();
                SummaryText = "正在加载消息...";
                hasShownNoMoreToast = false;

                var recentMessages = await SqliteHelper.Instance.GetRecentMessagesAsync(PageSize);
                foreach (var message in recentMessages)
                {
                    Messages.Add(message);
                }

                hasMoreMessages = recentMessages.Count == PageSize;
                SummaryText = $"已加载 {Messages.Count} 条消息";
            }
            catch
            {
                Messages.Clear();
                hasMoreMessages = false;
                SummaryText = "消息加载失败";
            }
            finally
            {
                loadLock.Release();
            }
        }

        async void OnMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
        {
            if (e.VerticalDelta <= 0 ||
                Messages.Count == 0 ||
                e.LastVisibleItemIndex < Messages.Count - 1)
            {
                return;
            }

            await LoadMoreMessagesAsync();
        }

        async Task LoadMoreMessagesAsync()
        {
            if (!hasMoreMessages)
            {
                await ShowNoMoreDataToastAsync();
                return;
            }

            if (!await loadLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                IsLoadingMore = true;
                var lastMessage = Messages[^1];
                var olderMessages = await SqliteHelper.Instance.GetMessagesBeforeAsync(
                    lastMessage.MsgDate,
                    lastMessage.ID,
                    PageSize);

                foreach (var message in olderMessages)
                {
                    Messages.Add(message);
                }

                hasMoreMessages = olderMessages.Count == PageSize;
                SummaryText = $"已加载 {Messages.Count} 条消息";

                if (olderMessages.Count == 0)
                {
                    await ShowNoMoreDataToastAsync();
                }
            }
            catch
            {
                SummaryText = "更多消息加载失败";
            }
            finally
            {
                IsLoadingMore = false;
                loadLock.Release();
            }
        }

        async Task ShowNoMoreDataToastAsync()
        {
            if (isToastVisible || hasShownNoMoreToast)
            {
                return;
            }

            isToastVisible = true;
            hasShownNoMoreToast = true;
            try
            {
                NoMoreDataToast.IsVisible = true;
                await NoMoreDataToast.FadeToAsync(1, 150);
                await Task.Delay(1500);
                await NoMoreDataToast.FadeToAsync(0, 150);
            }
            finally
            {
                NoMoreDataToast.IsVisible = false;
                isToastVisible = false;
            }
        }

        async void OnMessageSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not HxPushMsgModel message)
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
}
