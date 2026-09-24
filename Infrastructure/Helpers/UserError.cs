public sealed class UserError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
