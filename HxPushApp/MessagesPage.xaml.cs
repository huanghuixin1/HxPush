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
        const string AllDevicesFilter = "全部设备";

        readonly SemaphoreSlim loadLock = new(1, 1);
        bool hasMoreMessages = true;
        bool isLoadingMore;
        bool isRefreshing;
        bool isUpdatingDeviceFilters;
        bool isDeviceFilterInitialized;
        bool isToastVisible;
        bool hasShownNoMoreToast;
        string? activeDeviceId;

        public ObservableCollection<HxPushMsgModel> Messages { get; } = new();
        public ObservableCollection<string> DeviceFilters { get; } = new();

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
            DeviceFilters.Add(AllDevicesFilter);
            InitializeComponent();
            BindingContext = this;
            DeviceFilterPicker.SelectedIndex = 0;
            isDeviceFilterInitialized = true;
            Loaded += OnLoaded;
        }

        async void OnLoaded(object? sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            await RefreshFromUiAsync();
        }

        async void OnRefreshing(object? sender, EventArgs e)
        {
            await RefreshFromUiAsync();
        }

        async void OnRefreshButtonClicked(object? sender, EventArgs e)
        {
            await RefreshFromUiAsync();
        }

        async void OnDeviceFilterChanged(object? sender, EventArgs e)
        {
            if (!isDeviceFilterInitialized || isUpdatingDeviceFilters)
            {
                return;
            }

            await RefreshFromUiAsync();
        }

        async Task RefreshFromUiAsync()
        {
            if (isRefreshing)
            {
                return;
            }

            isRefreshing = true;
            RefreshMessagesButton.IsEnabled = false;
            DeviceFilterPicker.IsEnabled = false;
            MessagesRefreshView.IsRefreshing = true;

            try
            {
                await RefreshMessagesAsync();
            }
            finally
            {
                MessagesRefreshView.IsRefreshing = false;
                RefreshMessagesButton.IsEnabled = true;
                DeviceFilterPicker.IsEnabled = true;
                isRefreshing = false;
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

                await UpdateDeviceFiltersAsync();
                activeDeviceId = GetSelectedDeviceId();

                var recentMessages = await SqliteHelper.Instance.GetRecentMessagesAsync(
                    PageSize,
                    activeDeviceId);
                foreach (var message in OrderMessagesDescending(recentMessages))
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
                    PageSize,
                    activeDeviceId);

                foreach (var message in OrderMessagesDescending(olderMessages))
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

        async Task UpdateDeviceFiltersAsync()
        {
            var selectedFilter = DeviceFilterPicker.SelectedItem as string ?? AllDevicesFilter;
            var deviceIds = await SqliteHelper.Instance.GetDeviceIdsAsync();

            isUpdatingDeviceFilters = true;
            try
            {
                DeviceFilters.Clear();
                DeviceFilters.Add(AllDevicesFilter);

                foreach (var deviceId in deviceIds)
                {
                    DeviceFilters.Add(deviceId);
                }

                DeviceFilterPicker.SelectedItem = DeviceFilters.Contains(selectedFilter)
                    ? selectedFilter
                    : AllDevicesFilter;
            }
            finally
            {
                isUpdatingDeviceFilters = false;
            }
        }

        string? GetSelectedDeviceId()
        {
            var selectedFilter = DeviceFilterPicker.SelectedItem as string;
            return string.IsNullOrWhiteSpace(selectedFilter) || selectedFilter == AllDevicesFilter
                ? null
                : selectedFilter;
        }

        static IOrderedEnumerable<HxPushMsgModel> OrderMessagesDescending(
            IEnumerable<HxPushMsgModel> messages)
        {
            return messages
                .OrderByDescending(message => message.MsgDate)
                .ThenByDescending(message => message.ID, StringComparer.Ordinal);
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
