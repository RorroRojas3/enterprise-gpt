namespace Enterprise.Gpt.Dto
{
    public class PageEmbeddingDto
    {
        public int Number { get; set; }

        public string Text { get; set; } = null!;

        public ReadOnlyMemory<float> Embedding { get; set; }
    }
}
