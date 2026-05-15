using RR.AI_Chat.Common.Extensions;
using RR.AI_Chat.Dto.Enums;

namespace RR.AI_Chat.Dto
{
    public class FileDto
    {
        public string FileName { get; set; } = null!;

        public byte[] Content { get; set; } = [];

        public string ContentType { get; set; } = null!;

        public long Length { get; set; }

        public FileExtensions FileExtension => GetFileExtensionFromFileName();

        private FileExtensions GetFileExtensionFromFileName()
        {
            var extension = Path.GetExtension(FileName)?.ToLowerInvariant();

            if (string.IsNullOrEmpty(extension))
            {
                throw new InvalidOperationException($"Unable to determine file extension from filename: {FileName}");
            }

            foreach (FileExtensions enumValue in Enum.GetValues<FileExtensions>())
            {
                if (enumValue.GetDescription() == extension)
                {
                    return enumValue;
                }
            }

            throw new NotSupportedException($"File extension '{extension}' is not supported.");
        }
    }
}