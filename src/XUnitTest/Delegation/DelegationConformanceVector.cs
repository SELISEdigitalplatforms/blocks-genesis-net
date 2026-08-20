namespace XUnitTest.Delegation;

/// <summary>
/// The cross-SDK signature conformance vector. blocks-genesis-py asserts the same five inputs and
/// the same expected signature, so a divergence in either SDK fails a test rather than a
/// production exchange.
/// <para>
/// Keep in sync with <c>tests/test_delegation_conformance.py</c> in blocks-genesis-py.
/// </para>
/// </summary>
internal static class DelegationConformanceVector
{
    public const string TenantId = "tenant-abc";
    public const string DelegationId = "dg_00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    public const string Nonce = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    public const long Ts = 1739577600L;
    public const string TenantSalt = "d3f1c0de-5a17-4b0c-9e8a-1f2b3c4d5e6f";

    public const string ExpectedSignatureInput =
        "tenant-abc|dg_00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff|0f1e2d3c4b5a69788796a5b4c3d2e1f0|1739577600";

    public const string ExpectedSignature = "c01a5f122b9793b09385796b95f00ec3ebb28528d1043dc96cf3a9fe7628d560";
}
