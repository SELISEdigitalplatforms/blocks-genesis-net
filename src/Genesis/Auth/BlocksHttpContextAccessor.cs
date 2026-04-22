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

        public static void EnsureInitialized(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (Instance != null)
            {
                return;
            }

            var requestServices = context.RequestServices;
            if (requestServices == null)
            {
                return;
            }

            Instance = requestServices.GetService<IHttpContextAccessor>() ?? new HttpContextAccessor();
        }
    }
}
