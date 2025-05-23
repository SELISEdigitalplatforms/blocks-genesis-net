using Microsoft.AspNetCore.Http;

namespace Blocks.Genesis
{
    public static class BlocksHttpContextAccessor
    {
        public static IHttpContextAccessor? Instance { get; set; }
    }
}
