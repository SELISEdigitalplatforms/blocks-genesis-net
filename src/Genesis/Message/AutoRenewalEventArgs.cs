using Azure.Messaging.ServiceBus;

namespace Blocks.Genesis
{
    public class AutoRenewalEventArgs : EventArgs
    {
        public ProcessMessageEventArgs Args { get; set; }
        public CancellationToken Token { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
    }
}
