using System.ComponentModel;
using System.Runtime.CompilerServices;
using HxPushApp.models.Message;

namespace HxPushApp
{
    public partial class MessageDetailPage : ContentPage, IQueryAttributable, INotifyPropertyChanged
    {
        string id = string.Empty;
        string messageContent = string.Empty;
        string hwid = string.Empty;

        public string MessageNo => !string.IsNullOrWhiteSpace(id) ? $"消息 #{id}" : "消息详情";

        public string MessageContent
        {
            get => messageContent;
            set
            {
                if (messageContent == value)
                {
                    return;
                }

                messageContent = value;
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
            if (!query.TryGetValue("Message", out var value) || value is not HxPushMsgModel message)
            {
                return;
            }

            id = message.ID;
            MessageContent = message.Msg;
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
