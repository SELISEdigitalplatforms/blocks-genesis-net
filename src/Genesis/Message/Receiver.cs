using Azure.Messaging.ServiceBus;

namespace Blocks.Genesis
{
    public static class MessageReceiver
    {
        static async Task MessageHandler(ProcessMessageEventArgs args)
        {
            string body = args.Message.Body.ToString();
            Console.WriteLine($"Message received: {body}");

            await args.CompleteMessageAsync(args.Message);
        }

        static Task ErrorHandler(ProcessErrorEventArgs args)
        {
            Console.WriteLine(args.Exception.ToString());
            return Task.CompletedTask;
        }

        public static async Task ReceiveMessagesAsync()
        {
            await using ServiceBusClient client = new ServiceBusClient("Endpoint=sb://blocks-rnd.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=***REMOVED***");
            // create a processor that we can use to process the messages
            ServiceBusProcessor processor = client.CreateProcessor("DemoQueue", new ServiceBusProcessorOptions());

            // add handler to process messages
            processor.ProcessMessageAsync += MessageHandler;
            processor.ProcessErrorAsync += ErrorHandler;

            // start processing 
            await processor.StartProcessingAsync();


            Console.WriteLine("Wait for a minute and then press any key to end the processing");
            Console.ReadKey();
            
            // stop processing 
            await processor.StopProcessingAsync();
        }
        
    }
}
