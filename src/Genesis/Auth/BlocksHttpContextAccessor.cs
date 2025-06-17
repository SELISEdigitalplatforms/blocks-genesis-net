using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Blocks.Genesis
{
    public static class BlocksHttpContextAccessor
    {
        public static IHttpContextAccessor? Instance { get; set; }

        public static void Init(IServiceProvider serviceProvider)
        {
            Instance = serviceProvider.GetRequiredService<IHttpContextAccessor>();
        }
    }
}
