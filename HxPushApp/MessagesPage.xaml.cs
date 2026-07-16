using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HxPushApp.Helpers;
using HxPushSdk;
using HxPushApp.Helpers.Sqlite;
using HxPushApp.models.Message;

namespace HxPushApp
{
    public partial class MessagesPage : ContentPage, INotifyPropertyChanged
    {
        private const int PageSize = 50;
        private const string AllDevicesFilter = "全部设备";

        private readonly SemaphoreSlim loadLock = new(1, 1);
        private readonly HxPushMessageApiClient messageApiClient = HxPushMessageApiClient.Instance;
        private readonly PushConnectionService pushConnectionService = PushConnectionService.Instance;
        private readonly SqliteHelper sqliteHelper = SqliteHelper.Instance;

        private bool hasMoreMessages = true;
        private bool hasReachedRemoteEnd;
        private bool isLoadingMore;
        private bool isRefreshing;
        private bool isServerRequesting;
        private bool isUpdatingDeviceFilters;
        private bool isDeviceFilterInitialized;
        private bool isListAtTop = true;
        private bool isToastVisible;
        private bool hasShownNoMoreToast;
        private string? activeDeviceId;
        private string? remoteSyncError;
        private string summaryText = "正在加载消息...";
        private string loadingText = "正在加载...";

        public ObservableCollection<HxPushMsgModel> Messages { get; } = new();

        public ObservableCollection<string> DeviceFilters { get; } = new();

        public bool IsLoadingMore
        {
            get => isLoadingMore;
            private set => SetProperty(ref isLoadingMore, value);
        }

        public bool IsServerRequesting
        {
            get => isServerRequesting;
            private set => SetProperty(ref isServerRequesting, value);
        }

        public string LoadingText
        {
            get => loadingText;
            private set => SetProperty(ref loadingText, value);
        }

        public string SummaryText
        {
            get => summaryText;
            set => SetProperty(ref summaryText, value);
        }

        public MessagesPage()
        {
            DeviceFilters.Add(AllDevicesFilter);
            InitializeComponent();
            BindingContext = this;
            DeviceFilterPicker.SelectedIndex = 0;
            isDeviceFilterInitialized = true;
            pushConnectionService.PushMessagesReceived += OnPushMessagesReceived;
            sqliteHelper.DatabaseDeleted += OnLocalDatabaseDeleted;
            pushConnectionService.ConnectionStateChanged += OnConnectionStateChanged;
            UpdateConnectionStatus(pushConnectionService.IsConnected);
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object? sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            await RefreshFromUiAsync();
        }

        private async void OnRefreshing(object? sender, EventArgs e)
        {
            await RefreshFromUiAsync();
        }

        private async void OnRefreshButtonClicked(object? sender, EventArgs e)
        {
            await RefreshFromUiAsync();
        }

        private async void OnDeviceFilterChanged(object? sender, EventArgs e)
        {
            if (!isDeviceFilterInitialized || isUpdatingDeviceFilters)
            {
                return;
            }

            await RefreshFromUiAsync();
        }

        private async Task RefreshFromUiAsync()
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

        private async Task RefreshMessagesAsync()
        {
            await loadLock.WaitAsync();
            try
            {
                Messages.Clear();
                isListAtTop = true;
                LatestMessagesNotice.IsVisible = false;
                SummaryText = "正在加载消息...";
                hasShownNoMoreToast = false;
                hasReachedRemoteEnd = false;
                remoteSyncError = null;

                // 下拉刷新先向服务端拉取最新 50 条，再写入本地 SQLite；页面仍只从 SQLite 展示。
                activeDeviceId = GetSelectedDeviceId();
                var remoteCount = await TrySyncLatestMessagesFromServerAsync(activeDeviceId);
                if (remoteCount.HasValue)
                {
                    hasReachedRemoteEnd = remoteCount.Value < PageSize;
                }

                await UpdateDeviceFiltersAsync();
                activeDeviceId = GetSelectedDeviceId();

                var recentMessages = await Task.Run(() =>
                    SqliteHelper.Instance.GetRecentMessagesAsync(PageSize, activeDeviceId));
                AddMessages(recentMessages);

                hasMoreMessages = recentMessages.Count == PageSize || !hasReachedRemoteEnd;
                UpdateSummary();
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

        private async void OnMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
        {
            isListAtTop = e.FirstVisibleItemIndex <= 0;
            if (isListAtTop)
            {
                LatestMessagesNotice.IsVisible = false;
            }

            if (e.VerticalDelta <= 0 ||
                Messages.Count == 0 ||
                e.LastVisibleItemIndex < Messages.Count - 1)
            {
                return;
            }

            await LoadMoreMessagesAsync();
        }

        private void OnLatestMessagesNoticeTapped(object? sender, TappedEventArgs e)
        {
            if (Messages.Count > 0)
            {
                MessagesCollectionView.ScrollTo(
                    0,
                    position: ScrollToPosition.Start,
                    animate: true);
            }

            isListAtTop = true;
            LatestMessagesNotice.IsVisible = false;
        }

        private void OnConnectionStateChanged(object? sender, bool isConnected)
        {
            MainThread.BeginInvokeOnMainThread(() => UpdateConnectionStatus(isConnected));
        }

        private void UpdateConnectionStatus(bool isConnected)
        {
            WebSocketConnectionStatusLabel.Text = isConnected
                ? "已连接到服务器"
                : "已断开与服务器的连接";
            WebSocketConnectionStatusLabel.TextColor = isConnected
                ? Colors.ForestGreen
                : Colors.IndianRed;
        }

        private async Task LoadMoreMessagesAsync()
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
                remoteSyncError = null;

                var lastMessage = Messages[^1];
                var olderMessages = await GetLocalOlderMessagesAsync(lastMessage);

                // 本地没有更旧数据时，再按当前列表最后一条的游标去服务端拉取最多 50 条。
                if (olderMessages.Count == 0 && !hasReachedRemoteEnd)
                {
                    var remoteCount = await TrySyncOlderMessagesFromServerAsync(lastMessage);
                    if (remoteCount.HasValue && remoteCount.Value < PageSize)
                    {
                        hasReachedRemoteEnd = true;
                    }

                    olderMessages = await GetLocalOlderMessagesAsync(lastMessage);
                }

                var appendedCount = AddMessages(olderMessages);
                hasMoreMessages = appendedCount > 0
                    ? olderMessages.Count == PageSize || !hasReachedRemoteEnd
                    : !hasReachedRemoteEnd;
                UpdateSummary();

                if (appendedCount == 0 && hasReachedRemoteEnd)
                {
                    hasMoreMessages = false;
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

        private Task<IReadOnlyList<HxPushMsgModel>> GetLocalOlderMessagesAsync(
            HxPushMsgModel lastMessage)
        {
            return Task.Run(() => SqliteHelper.Instance.GetMessagesBeforeAsync(
                lastMessage.MsgDate,
                lastMessage.ID,
                PageSize,
                activeDeviceId));
        }

        private async Task<int?> TrySyncLatestMessagesFromServerAsync(string? hwid)
        {
            IsServerRequesting = true;
            LoadingText = "正在从服务端同步消息...";

            try
            {
                return await Task.Run(async () =>
                {
                    var remoteMessages = await messageApiClient.GetMessagesAsync(PageSize, hwid)
                        .ConfigureAwait(false);
                    await SqliteHelper.Instance.SaveMessagesAsync(remoteMessages)
                        .ConfigureAwait(false);
                    return remoteMessages.Count;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                remoteSyncError = "请求超时（10秒），已显示本地数据";
                return null;
            }
            catch (Exception ex)
            {
                remoteSyncError = $"服务端同步失败：{ex.Message}";
                return null;
            }
            finally
            {
                IsServerRequesting = false;
            }
        }

        private async Task<int?> TrySyncOlderMessagesFromServerAsync(HxPushMsgModel lastMessage)
        {
            try
            {
                return await Task.Run(async () =>
                {
                    var remoteMessages = await messageApiClient.GetMessagesAsync(
                            PageSize,
                            activeDeviceId,
                            new HxPushMessageCursor(lastMessage.MsgDate, lastMessage.ID))
                        .ConfigureAwait(false);
                    await SqliteHelper.Instance.SaveMessagesAsync(remoteMessages)
                        .ConfigureAwait(false);
                    return remoteMessages.Count;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                remoteSyncError = "加载更多超时（10秒），已显示本地数据";
                return null;
            }
            catch (Exception ex)
            {
                remoteSyncError = $"服务端加载更多失败：{ex.Message}";
                return null;
            }
        }

        private async Task UpdateDeviceFiltersAsync()
        {
            var selectedFilter = DeviceFilterPicker.SelectedItem as string ?? AllDevicesFilter;
            var deviceIds = await Task.Run(() => SqliteHelper.Instance.GetDeviceIdsAsync());

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

        private string? GetSelectedDeviceId()
        {
            var selectedFilter = DeviceFilterPicker.SelectedItem as string;
            return string.IsNullOrWhiteSpace(selectedFilter) || selectedFilter == AllDevicesFilter
                ? null
                : selectedFilter;
        }

        private void OnPushMessagesReceived(
            object? sender,
            IReadOnlyList<HxPushMsgModel> pushMessages)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Any successfully persisted WebSocket push should notify the user,
                // even when it does not match the currently selected device filter.
                LatestMessagesNotice.IsVisible = true;

                foreach (var message in pushMessages)
                {
                    if (!DeviceFilters.Contains(message.Hwid))
                    {
                        DeviceFilters.Add(message.Hwid);
                    }
                }

                var selectedDeviceId = GetSelectedDeviceId();
                var messagesForSelectedDevice = pushMessages.Where(message =>
                    string.IsNullOrWhiteSpace(selectedDeviceId) ||
                    string.Equals(message.Hwid, selectedDeviceId, StringComparison.Ordinal));

                var addedCount = AddMessages(messagesForSelectedDevice);
                if (addedCount == 0)
                {
                    return;
                }

                UpdateSummary();
            });
        }

        private void OnLocalDatabaseDeleted(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Clear();
                activeDeviceId = null;
                hasMoreMessages = false;
                hasReachedRemoteEnd = true;
                LatestMessagesNotice.IsVisible = false;

                isUpdatingDeviceFilters = true;
                try
                {
                    DeviceFilters.Clear();
                    DeviceFilters.Add(AllDevicesFilter);
                    DeviceFilterPicker.SelectedIndex = 0;
                }
                finally
                {
                    isUpdatingDeviceFilters = false;
                }

                SummaryText = "本地缓存已删除";
            });
        }

        private int AddMessages(IEnumerable<HxPushMsgModel> messages)
        {
            var appendedCount = 0;

            foreach (var message in OrderMessagesDescending(messages))
            {
                var existingIndex = FindMessageIndex(message.ID);
                if (existingIndex >= 0)
                {
                    Messages.RemoveAt(existingIndex);
                    Messages.Insert(FindInsertIndex(message), message);
                    continue;
                }

                Messages.Insert(FindInsertIndex(message), message);
                appendedCount++;
            }

            return appendedCount;
        }

        private int FindMessageIndex(string messageId)
        {
            for (var index = 0; index < Messages.Count; index++)
            {
                if (string.Equals(Messages[index].ID, messageId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private int FindInsertIndex(HxPushMsgModel message)
        {
            for (var index = 0; index < Messages.Count; index++)
            {
                var existingMessage = Messages[index];
                var isOlderThanExisting = message.MsgDate < existingMessage.MsgDate ||
                    (message.MsgDate == existingMessage.MsgDate &&
                     string.Compare(message.ID, existingMessage.ID, StringComparison.Ordinal) < 0);

                if (isOlderThanExisting)
                {
                    continue;
                }

                return index;
            }

            return Messages.Count;
        }

        private void UpdateSummary()
        {
            SummaryText = remoteSyncError is null
                ? $"已加载 {Messages.Count} 条消息"
                : $"已加载 {Messages.Count} 条本地消息，{remoteSyncError}";
        }

        private static IOrderedEnumerable<HxPushMsgModel> OrderMessagesDescending(
            IEnumerable<HxPushMsgModel> messages)
        {
            return messages
                .OrderByDescending(message => message.MsgDate)
                .ThenByDescending(message => message.ID, StringComparer.Ordinal);
        }

        private async Task ShowNoMoreDataToastAsync()
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

        private async void OnMessageSelected(object? sender, SelectionChangedEventArgs e)
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

        private bool SetProperty<T>(
            ref T backingStore,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
            {
                return false;
            }

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
