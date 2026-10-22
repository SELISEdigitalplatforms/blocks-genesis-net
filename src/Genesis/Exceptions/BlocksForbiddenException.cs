namespace Blocks.Genesis;

/// <summary>
/// The caller is known but not allowed to do this (HTTP 403). <see cref="Code"/> is the stable error code
/// clients read; data-access policy denials use <see cref="DataAccess.Evaluation.PolicyErrorCodes.Forbidden"/>.
/// </summary>
public sealed class BlocksForbiddenException : BlocksException
{
    public BlocksForbiddenException(string message) : this(message, DataAccess.Evaluation.PolicyErrorCodes.Forbidden)
    {
    }

    public BlocksForbiddenException(string message, Exception innerException) : base(message, innerException)
    {
        Code = DataAccess.Evaluation.PolicyErrorCodes.Forbidden;
    }

    public BlocksForbiddenException(string message, string code) : base(message)
    {
        Code = code;
    }

    /// <summary>Stable error code, e.g. <c>FORBIDDEN</c>.</summary>
    public string Code { get; }
}
