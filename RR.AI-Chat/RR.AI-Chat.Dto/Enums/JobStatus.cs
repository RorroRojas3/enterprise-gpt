using System.ComponentModel;

namespace RR.AI_Chat.Dto.Enums
{
    public enum JobStatus
    {
        [Description(nameof(Queued))]
        Queued = 1,

        [Description(nameof(Uploading))]
        Uploading = 2,

        [Description(nameof(Extracting))]
        Extracting = 3,

        [Description(nameof(Embedding))]
        Embedding = 4,

        [Description(nameof(Processed))]
        Processed = 5,

        [Description(nameof(Failed))]
        Failed = 6,
    }
}
