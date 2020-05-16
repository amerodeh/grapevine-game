using System.Collections.Generic;

namespace Grapevine.GameRunner.Models
{
    public class MessageRequest
    {
        public int GameId { get; set; }

        public string Message { get; set; }

        public int SentFromId { get; set; }

        public int NextWhisperRecipientId { get; set; }

        public List<WhisperRecipient> WhisperRecipients { get; set; }
    }

    public class WhisperRecipient
    {
        public int Id { get; set; }

        public string Url { get; set; }
    }
}
