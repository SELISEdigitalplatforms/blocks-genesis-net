using Azure.Messaging.ServiceBus;

namespace Blocks.Genesis
{
    public static class MessageSender
    {
        public static async Task SendMessagesAsync()
        {

            try
            {
                await using ServiceBusClient client = new ServiceBusClient("Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***");
                ServiceBusSender sender = client.CreateSender("DemoQueue");
                ServiceBusMessage message = new ServiceBusMessage("It Is a demo");
                await sender.SendMessageAsync(message);

                sender = client.CreateSender("DemoQueue1");
                message = new ServiceBusMessage("It Is a demo 1");
                await sender.SendMessageAsync(message);
            }
            catch (Exception e)
            {

                Console.WriteLine(e);
            }

        }
    }
}
