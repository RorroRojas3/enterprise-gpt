namespace Enterprise.Gpt.Service.Exceptions
{
    /// <summary>
    /// Thrown when the caller is authenticated but lacks the permission required for the
    /// requested resource (e.g. using an MCP server without holding its permission).
    /// Mapped to HTTP 403 by the global exception handler.
    /// </summary>
    public class ForbiddenException : Exception
    {
        public ForbiddenException(string message) : base(message) { }

        public ForbiddenException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
