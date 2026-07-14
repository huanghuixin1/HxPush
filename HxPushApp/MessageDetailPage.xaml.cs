using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HxPushApp
{
    public partial class MessageDetailPage : ContentPage, IQueryAttributable, INotifyPropertyChanged
    {
        int id;
        string content = string.Empty;
        string hwid = string.Empty;

        public string MessageNo => id > 0 ? $"消息 #{id}" : "消息详情";

        public string Content
        {
            get => content;
            set
            {
                if (content == value)
                {
                    return;
                }

                content = value;
                OnPropertyChanged();
            }
        }

        public string Hwid
        {
            get => hwid;
            set
            {
                if (hwid == value)
                {
                    return;
                }

                hwid = value;
                OnPropertyChanged();
            }
        }

        public MessageDetailPage()
        {
            InitializeComponent();
            BindingContext = this;
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            if (!query.TryGetValue("Message", out var value) || value is not MessageItem message)
            {
                return;
            }

            id = message.Id;
            Content = message.Content;
            Hwid = message.Hwid;
            OnPropertyChanged(nameof(MessageNo));
        }

        public new event PropertyChangedEventHandler? PropertyChanged;

        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
